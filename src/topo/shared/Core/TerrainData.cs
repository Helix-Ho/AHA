using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Aha.Topo;

/// <summary>The SDK's south-origin tile index; array rows run north to south.</summary>
public sealed record TerrainTile
{
    public const int Width = 65;
    public const int ValueCount = Width * Width;
    public const int ByteCount = ValueCount * sizeof(float);

    public int Level { get; }
    public int X { get; }
    public int Y { get; }
    public float[] Heights { get; }
    public double SpanDegrees => 36.0 / (1 << Level);
    public double CellLongitudeDegrees => SpanDegrees / (Width - 1);
    public double CellLatitudeDegrees => CellLongitudeDegrees;
    public GeoBounds Bounds => new(X * SpanDegrees - 180.0, Y * SpanDegrees - 90.0,
        (X + 1) * SpanDegrees - 180.0, (Y + 1) * SpanDegrees - 90.0);

    public TerrainTile(int level, int x, int y, float[] heights)
    {
        ValidateLevel(level);
        if (x < 0 || x >= (10 << level) || y < 0 || y >= (5 << level))
            throw new ArgumentOutOfRangeException(nameof(x), "지형 타일 인덱스가 범위를 벗어났습니다.");
        if (heights is null || heights.Length != ValueCount || heights.Any(z => !float.IsFinite(z)))
            throw new ArgumentException("지형 타일에는 유한한 높이 4,225개가 필요합니다.", nameof(heights));
        Level = level;
        X = x;
        Y = y;
        Heights = heights;
    }

    public static void ValidateLevel(int level)
    {
        if (level < 0 || level > 15)
            throw new ArgumentOutOfRangeException(nameof(level), "지형 레벨은 0~15여야 합니다.");
    }

    public static (int X, int Y) IndexAt(GeoPoint point, int level)
    {
        ArgumentNullException.ThrowIfNull(point);
        ValidateLevel(level);
        if (!double.IsFinite(point.Lon) || !double.IsFinite(point.Lat)
            || point.Lon < -180 || point.Lon > 180 || point.Lat < -90 || point.Lat > 90)
            throw new ArgumentOutOfRangeException(nameof(point), "유효한 경위도가 필요합니다.");
        double span = 36.0 / (1 << level);
        return (Math.Min((int)Math.Floor((point.Lon + 180) / span), (10 << level) - 1),
            Math.Min((int)Math.Floor((point.Lat + 90) / span), (5 << level) - 1));
    }

    public static float[] Decode(ReadOnlySpan<byte> body)
    {
        if (body.Length != ByteCount)
            throw new InvalidDataException("선택한 레벨의 지형 타일이 없거나 응답 형식이 올바르지 않습니다.");
        var values = new float[ValueCount];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(body.Slice(i * 4, 4)));
            if (!float.IsFinite(values[i]))
                throw new InvalidDataException("지형 응답에 유효하지 않은 높이가 포함되어 있습니다.");
        }
        return values;
    }
}

/// <summary>A complete, same-level rectangle. No interpolation across missing tiles.</summary>
public sealed class TerrainGrid
{
    public const int MaximumTiles = 64;
    public const double SeamToleranceMetres = 0.01;
    private readonly Dictionary<(int X, int Y), TerrainTile> _byIndex;
    private readonly int _minimumX;
    private readonly int _maximumX;
    private readonly int _minimumY;
    private readonly int _maximumY;

    public IReadOnlyList<TerrainTile> Tiles { get; }
    public int Level { get; }
    public GeoBounds Bounds { get; }
    public double CellLongitudeDegrees { get; }
    public double CellLatitudeDegrees => CellLongitudeDegrees;

    public TerrainGrid(IEnumerable<TerrainTile> tiles)
    {
        var list = tiles?.ToList() ?? throw new ArgumentNullException(nameof(tiles));
        if (list.Count == 0 || list.Count > MaximumTiles)
            throw new ArgumentException("지형은 1~64개의 타일로 구성되어야 합니다.", nameof(tiles));
        Level = list[0].Level;
        if (list.Any(tile => tile.Level != Level))
            throw new ArgumentException("서로 다른 레벨의 지형 타일을 섞을 수 없습니다.", nameof(tiles));
        _byIndex = new Dictionary<(int, int), TerrainTile>();
        foreach (var tile in list)
            if (!_byIndex.TryAdd((tile.X, tile.Y), tile))
                throw new ArgumentException("중복 지형 타일이 있습니다.", nameof(tiles));
        _minimumX = list.Min(tile => tile.X);
        _maximumX = list.Max(tile => tile.X);
        _minimumY = list.Min(tile => tile.Y);
        _maximumY = list.Max(tile => tile.Y);
        if ((long)(_maximumX - _minimumX + 1) * (_maximumY - _minimumY + 1) != list.Count)
            throw new ArgumentException("지형 범위에 누락된 타일이 있습니다.", nameof(tiles));
        double span = list[0].SpanDegrees;
        Bounds = new GeoBounds(_minimumX * span - 180, _minimumY * span - 90,
            (_maximumX + 1) * span - 180, (_maximumY + 1) * span - 90);
        CellLongitudeDegrees = span / (TerrainTile.Width - 1);
        ValidateSeams();
        Tiles = new ReadOnlyCollection<TerrainTile>(list);
    }

    private void ValidateSeams()
    {
        foreach (var tile in _byIndex.Values)
        {
            if (_byIndex.TryGetValue((tile.X + 1, tile.Y), out var east))
                for (int row = 0; row < TerrainTile.Width; row++)
                    CheckSeam(tile.Heights[row * TerrainTile.Width + 64], east.Heights[row * TerrainTile.Width]);
            if (_byIndex.TryGetValue((tile.X, tile.Y + 1), out var north))
                for (int column = 0; column < TerrainTile.Width; column++)
                    CheckSeam(tile.Heights[column], north.Heights[64 * TerrainTile.Width + column]);
        }
    }

    private static void CheckSeam(float first, float second)
    {
        if (Math.Abs((double)first - second) > SeamToleranceMetres)
            throw new InvalidDataException("인접 지형 타일의 경계 높이가 일치하지 않습니다. 모델링을 중단했습니다.");
    }

    public double Sample(GeoPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (!double.IsFinite(point.Lon) || !double.IsFinite(point.Lat)
            || point.Lon < Bounds.West || point.Lon > Bounds.East
            || point.Lat < Bounds.South || point.Lat > Bounds.North)
            throw new ArgumentOutOfRangeException(nameof(point), "조회한 지형 범위를 벗어난 위치입니다.");
        var index = TerrainTile.IndexAt(point, Level);
        // At the outer east/north boundary, use the last tile's shared edge.
        index = (Math.Clamp(index.X, _minimumX, _maximumX), Math.Clamp(index.Y, _minimumY, _maximumY));
        if (!_byIndex.TryGetValue(index, out var tile))
            throw new InvalidDataException("이 위치에 지형 데이터가 없습니다.");
        var bounds = tile.Bounds;
        double column = Math.Clamp((point.Lon - bounds.West) / CellLongitudeDegrees, 0, 64);
        double row = Math.Clamp((bounds.North - point.Lat) / CellLatitudeDegrees, 0, 64);
        int x0 = Math.Min((int)Math.Floor(column), 63);
        int y0 = Math.Min((int)Math.Floor(row), 63);
        double tx = column - x0, ty = row - y0;
        double northwest = tile.Heights[y0 * TerrainTile.Width + x0];
        double northeast = tile.Heights[y0 * TerrainTile.Width + x0 + 1];
        double southwest = tile.Heights[(y0 + 1) * TerrainTile.Width + x0];
        double southeast = tile.Heights[(y0 + 1) * TerrainTile.Width + x0 + 1];
        double value = (northwest * (1 - tx) + northeast * tx) * (1 - ty)
            + (southwest * (1 - tx) + southeast * tx) * ty;
        if (!double.IsFinite(value))
            throw new InvalidDataException("이 위치의 지형 높이가 유효하지 않습니다.");
        return value;
    }
}

