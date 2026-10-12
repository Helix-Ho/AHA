using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Aha.EumCrawl;

/// <summary>Reads public Eum pages. No browser execution, login, cookies, AI, or arbitrary target URLs.</summary>
public sealed class EumClient : IDisposable
{
    public const string Origin = "https://www.eum.go.kr";
    private const string Path = "/web/ar/lu/";
    private const int MaxBytes = 2 * 1024 * 1024;
    private readonly HttpClient http;
    public EumClient() : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) { }
    public EumClient(HttpMessageHandler handler) : this(handler, TimeSpan.FromSeconds(30)) { }
    internal EumClient(HttpMessageHandler handler, TimeSpan requestTimeout)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        http = new HttpClient(handler) { Timeout = requestTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AHA-EumCrawl/1.0");
    }
    public void Dispose() => http.Dispose();

    public static string ParcelUrl(string pnu)
    {
        if (!EumParser.IsPnu(pnu)) throw new ArgumentException("PNU는 일반/산 구분을 포함한 19자리 숫자여야 합니다.");
        return Origin + Path + "luLandDet.jsp?pnu=" + pnu + "&mode=search&selGbn=umd&isNoScr=script";
    }

    public async Task<JsonObject> LookupAsync(string pnu, CancellationToken ct = default)
    {
        var acquired = await AcquireAsync(pnu, EumEvidence.SectionNames, ct).ConfigureAwait(false);
        var main = acquired.Main.Html;
        var url = acquired.Main.Url;
        var retrieved = acquired.Main.RetrievedAt;
        var sources = new JsonArray();
        var warnings = new JsonArray(EumParser.RatioPolicyNotice);
        var pages = new Dictionary<string, string>();
        foreach (var page in acquired.Pages.Where(p => p.Code.Length > 0))
        {
            pages[page.Code] = page.Html;
            if (page.Error is not null) { warnings.Add(page.Title + " 조회 실패: 원문 페이지에서 직접 확인하세요."); continue; }
            sources.Add(new JsonObject { ["url"] = page.Url, ["title"] = "토지이음 · " + page.Title,
                ["retrieved_at"] = page.RetrievedAt, ["reference_date"] = EumParser.ReferenceDate(page.Html, page.Code),
                ["text"] = EumParser.Text(page.Html), ["pnu"] = pnu });
        }
        var fields = EumParser.ParseFields(pages["GY"], pages["C"], pages["K"], pages["K2"]);
        foreach (var source in sources.OfType<JsonObject>())
        {
            var names = source["url"]!.GetValue<string>().Contains("carGbn=GY", StringComparison.Ordinal)
                ? new[] { "bcr_percent", "far_percent" } : source["url"]!.GetValue<string>().Contains("carGbn=C", StringComparison.Ordinal)
                ? new[] { "max_height_m", "max_floors" } : Array.Empty<string>();
            var texts = new JsonObject();
            foreach (var name in names)
                if (fields[name]?["status"]?.GetValue<string>() is "candidate" or "conditional")
                    texts[name] = fields[name]?["source_text"]?.GetValue<string>() ?? EumParser.FieldLabel(name) + ": " + fields[name]?["text"];
            source["field_texts"] = texts;
        }
        var reference = EumParser.ParcelReferenceText(main);
        var parcelFields = EumParser.ParseParcelFields(main);
        var additionalReference = EumParser.AdditionalReferenceText(main);
        if (reference.Length > 0)
        {
            var texts = new JsonObject();
            foreach (var field in EumParser.ParcelFields)
                if (parcelFields[field]?["status"]?.GetValue<string>() == "candidate")
                    texts["parcels." + field] = parcelFields[field]!["text"]!.DeepClone();
            if (additionalReference.Length > 0) texts["notes"] = additionalReference;
            sources.Insert(0, new JsonObject { ["url"] = url, ["title"] = "토지이음 · 토지이용계획 참고",
                ["retrieved_at"] = retrieved, ["reference_date"] = "", ["text"] = reference, ["pnu"] = pnu,
                ["field_texts"] = texts });
        }
        return new JsonObject { ["pnu"] = pnu, ["address"] = EumParser.ParcelAddress(main), ["url"] = url,
            ["reference_text"] = reference, ["additional_reference_text"] = additionalReference,
            ["parcel_fields"] = parcelFields, ["fields"] = fields,
            ["sources"] = sources, ["warnings"] = warnings };
    }

    /// <summary>Read source sections without numerical selection, preview tokens or object writes.</summary>
    public async Task<JsonObject> ReadEvidenceAsync(string pnu, string[]? sections = null, CancellationToken ct = default)
    {
        var selected = EumEvidence.ValidateSections(sections);
        var acquired = await AcquireAsync(pnu, selected, ct).ConfigureAwait(false);
        return EumEvidence.Render(pnu, acquired.Main, acquired.Pages);
    }

    private async Task<(EumSourcePage Main, IReadOnlyList<EumSourcePage> Pages)> AcquireAsync(
        string pnu, IReadOnlyList<string> sections, CancellationToken ct)
    {
        var url = ParcelUrl(pnu);
        var mainHtml = await ReadAsync(url, ct).ConfigureAwait(false);
        EumParser.ValidateParcel(mainHtml, pnu);
        var main = new EumSourcePage("parcel", "필지 정보", "", url, mainHtml, DateTimeOffset.UtcNow.ToString("O"));
        var codes = EumParser.Input(mainHtml, "ucodes");
        var markCodes = EumParser.MarkCodes(mainHtml);
        if (new[] { codes, markCodes }.Any(value => value.Length > 4000 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c != ';')))
            throw new InvalidDataException("토지이음 지역지구 코드 응답 형식이 바뀌었습니다.");
        var pages = new List<EumSourcePage>();
        foreach (var section in sections)
        {
            if (section == "parcel") { pages.Add(main); continue; }
            var (code, path, title) = section switch
            {
                "bcr_far" => ("GY", "luLandDetUseGYAjax.jsp", "건폐율·용적률"),
                "height_floors" => ("C", "luLandDetUseCAjax.jsp", "층수·높이제한"),
                "building_line" => ("K", "luLandDetUseKDAjax.jsp", "건축선"),
                "open_space" => ("K2", "luLandDetUseKDAjax.jsp", "대지 안의 공지"),
                _ => throw new ArgumentException("지원하지 않는 토지이음 항목입니다.")
            };
            ct.ThrowIfCancellationRequested();
            var sourceUrl = Origin + Path + path + "?ucodes=" + Uri.EscapeDataString(codes) +
                "&sggcd=" + pnu[..5] + "&pnu=" + pnu + "&carGbn=" + code +
                (code == "C" ? "&markUcodes=" + Uri.EscapeDataString(markCodes) : "");
            try
            {
                var html = await ReadAsync(sourceUrl, ct).ConfigureAwait(false);
                if (!html.Contains("규제", StringComparison.Ordinal) && !html.Contains("base_date", StringComparison.Ordinal))
                    throw new InvalidDataException("규제 응답을 확인하지 못했습니다.");
                var returnedPnu = EumParser.Input(html, "pnu");
                if (returnedPnu.Length > 0 && returnedPnu != pnu) throw new InvalidDataException("규제 응답의 PNU가 조회 필지와 다릅니다.");
                if (EumParser.Text(html).Length > 30000) throw new InvalidDataException("규제 원문이 처리 한도를 초과했습니다.");
                pages.Add(new EumSourcePage(section, title, code, sourceUrl, html, DateTimeOffset.UtcNow.ToString("O")));
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                pages.Add(new EumSourcePage(section, title, code, sourceUrl, "", DateTimeOffset.UtcNow.ToString("O"),
                    ex is InvalidDataException ? ex.Message : "공개 자료 요청이 실패했습니다. 원문 페이지에서 확인하세요."));
            }
        }
        return (main, pages);
    }

    private async Task<string> ReadAsync(string uri, CancellationToken ct)
    {
        // ResponseHeadersRead ends HttpClient's timeout at the headers. Keep the deadline through the body.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(http.Timeout);
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("공개 데이터 조회가 실패했습니다.", null, response.StatusCode);
            if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("응답 크기가 처리 한도를 초과했습니다.");
            await using var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var block = new byte[8192];
            int count;
            while ((count = await source.ReadAsync(block, deadline.Token).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + count > MaxBytes) throw new InvalidDataException("응답 크기가 처리 한도를 초과했습니다.");
                buffer.Write(block, 0, count);
            }
            var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"').ToLowerInvariant();
            var encoding = charset is "euc-kr" or "ks_c_5601-1987" or "cp949" ||
                charset is null && uri.StartsWith(Origin, StringComparison.Ordinal) ? Encoding.GetEncoding(949) : Encoding.UTF8;
            return encoding.GetString(buffer.ToArray());
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        { throw new HttpRequestException("공개 데이터 응답 시간이 초과되었습니다.", ex); }
    }
}
