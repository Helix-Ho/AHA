using System.Runtime.InteropServices;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.PlugIns;
using Rhino.UI;
using Command = Rhino.Commands.Command;

[assembly: Guid("54b2e3fd-c674-479d-9aef-839a93756d72")]
[assembly: PlugInDescription(DescriptionType.Organization, "AHA")]
[assembly: PlugInDescription(DescriptionType.Email, "")]

namespace Aha.Runtime;

public sealed class AhaPlugin : PlugIn
{
    public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;
    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
        Register(typeof(Launcher), "AHA", "aha");
        Register(typeof(Topo.TopoPanel), "AHA · Topo", "topo");
        Register(typeof(Info.InfoPanel), "AHA · Info", "info");
        Register(typeof(EumCrawl.EumPanel), "AHA · EumCrawl", "eum");
        Register(typeof(Setting.SettingPanel), "AHA · Setting", "setting");
        RhinoApp.Idle += StartServices;
        RhinoDoc.CloseDocument += DocumentClosed;
        return LoadReturnCode.Success;
    }
    private void Register(Type type, string caption, string icon) =>
        Panels.RegisterPanel(this, type, caption, typeof(AhaPlugin).Assembly,
            $"Aha.Icons.{icon}.ico", PanelType.PerDoc);
    private void StartServices(object? sender, EventArgs e)
    {
        RhinoApp.Idle -= StartServices;
        McpControl.ApplySettings();
    }
    private static void DocumentClosed(object? sender, DocumentEventArgs e) => Jobs.CancelDocument(e.DocumentSerialNumber);
    protected override void OnShutdown()
    {
        RhinoApp.Idle -= StartServices;
        RhinoDoc.CloseDocument -= DocumentClosed;
        McpControl.Stop(); Jobs.Shutdown();
        base.OnShutdown();
    }
}

[Guid("39024be4-8938-4304-b4c8-a4e6e89b7c32")]
public sealed class AhaCommand : Command
{
    public override string EnglishName => "AHA";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) { Launcher.Open(doc); return Result.Success; }
}
[Guid("e4845b3d-8a26-47cd-b00a-0246011603e3")]
public sealed class TopoCommand : Command
{
    public override string EnglishName => "AHATopo";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) { Topo.TopoPanel.Open(doc); return Result.Success; }
}
[Guid("332e204a-ce65-44da-97d2-97947f80b355")]
public sealed class SettingCommand : Command
{
    public override string EnglishName => "AHASetting";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) { Setting.SettingPanel.Open(doc); return Result.Success; }
}
[Guid("b7e40f2b-35bf-4ad9-a438-397bd57868d6")]
public sealed class InfoCommand : Command
{
    public override string EnglishName => "AHAInfo";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) { Info.InfoPanel.Open(doc); return Result.Success; }
}
[Guid("4609de4a-78b0-4fdb-af9c-ff4d113de9da")]
public sealed class EumCommand : Command
{
    public override string EnglishName => "AHAEumCrawl";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) { EumCrawl.EumPanel.Open(doc); return Result.Success; }
}

[Guid("d39b3e02-6e41-4a5c-9d02-c1d1b1693ca7")]
public sealed class Launcher : Panel
{
    public static void Open(RhinoDoc doc) => Aha.Runtime.ToolPanels.Open(doc, typeof(Launcher));
    public Launcher(uint documentSerialNumber)
    {
        var heading = new TableLayout
        {
            Rows = { new TableRow(
                new Label { Text = "AHA", Font = new Font(SystemFont.Bold, 22), TextAlignment = TextAlignment.Left },
                new TableCell(null, true),
                new Label { Text = Aha.Mcp.McpProtocol.ApplicationVersion + " · Rhino 8",
                    Font = new Font(SystemFont.Default, 10), TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center }) }
        };
        var tools = new TableLayout { Spacing = new Size(8, 10) };
        void Add(string title, string description, Action<RhinoDoc> open)
        {
            var button = new Button { Text = title, Width = 125 };
            button.Click += (_, _) => { var doc = RhinoDoc.FromRuntimeSerialNumber(documentSerialNumber); if (doc != null) open(doc); };
            tools.Rows.Add(new TableRow(button,
                new TableCell(new Label { Text = description, Wrap = WrapMode.Word }, true)));
        }
        Add("Topo", "지형·필지·주변 건물 모델링", Topo.TopoPanel.Open);
        Add("Info", "선택 객체의 정보와 대지 속성", Info.InfoPanel.Open);
        Add("EumCrawl", "토지이음 조회·선택 적용 · 실험", EumCrawl.EumPanel.Open);
        Add("Setting", "VWorld KEY·MCP 연결", Setting.SettingPanel.Open);
        Content = new TableLayout
        {
            Padding = 18, Spacing = new Size(8, 10),
            Rows = { heading, tools, new TableRow(new LogoView()) { ScaleHeight = true } }
        };
        Size = new Size(380, 330);
    }
}
