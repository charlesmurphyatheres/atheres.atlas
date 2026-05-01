using System.Text;

namespace Atheres.Atlas.Functions.Services;

/// <summary>
/// Encodes / decodes Google's "encoded polyline algorithm format"
/// (https://developers.google.com/maps/documentation/utilities/polylinealgorithm).
///
/// Used by the routing pipeline to stitch multiple Directions responses
/// into one continuous polyline — Google's encoding is delta-based, so
/// you can't simply concatenate two encoded strings without first
/// decoding to absolute points.
/// </summary>
public static class PolylineCodec
{
    /// <summary>
    /// Decodes an encoded polyline into a list of (lat, lng) points.
    /// Returns an empty list for a null/empty input.
    /// </summary>
    public static List<(double Lat, double Lng)> Decode(string? encoded)
    {
        var points = new List<(double, double)>();
        if (string.IsNullOrEmpty(encoded)) return points;

        var index = 0;
        long lat = 0;
        long lng = 0;
        while (index < encoded.Length)
        {
            lat += ReadVarint(encoded, ref index);
            lng += ReadVarint(encoded, ref index);
            points.Add((lat / 1e5, lng / 1e5));
        }
        return points;
    }

    /// <summary>
    /// Encodes a list of (lat, lng) points back into Google's encoded
    /// polyline string. Inverse of <see cref="Decode"/>.
    /// </summary>
    public static string Encode(IReadOnlyList<(double Lat, double Lng)> points)
    {
        var sb = new StringBuilder();
        long prevLat = 0;
        long prevLng = 0;
        foreach (var (lat, lng) in points)
        {
            var eLat = (long)Math.Round(lat * 1e5);
            var eLng = (long)Math.Round(lng * 1e5);
            WriteVarint(sb, eLat - prevLat);
            WriteVarint(sb, eLng - prevLng);
            prevLat = eLat;
            prevLng = eLng;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Concatenates two encoded polylines into one continuous polyline.
    /// If the last point of <paramref name="first"/> matches the first
    /// point of <paramref name="second"/> (within ~1 meter), the duplicate
    /// is dropped so the joint isn't a zero-length segment.
    /// </summary>
    public static string Concat(string? first, string? second)
    {
        if (string.IsNullOrEmpty(first))  return second ?? string.Empty;
        if (string.IsNullOrEmpty(second)) return first;

        var a = Decode(first);
        var b = Decode(second);
        if (a.Count == 0) return Encode(b);
        if (b.Count == 0) return Encode(a);

        var combined = new List<(double, double)>(a.Count + b.Count);
        combined.AddRange(a);

        // Skip the first point of the second polyline when it's effectively
        // the same as the last point of the first — typically the joint
        // location (warehouse, in our two-leg use case). 1e-5 degrees ≈ 1m.
        var (lastLat, lastLng) = a[^1];
        var (firstLat, firstLng) = b[0];
        var startIdx =
            Math.Abs(lastLat - firstLat) < 1e-5 && Math.Abs(lastLng - firstLng) < 1e-5
                ? 1
                : 0;
        for (var i = startIdx; i < b.Count; i++) combined.Add(b[i]);
        return Encode(combined);
    }

    // -----------------------------------------------------------------------
    private static long ReadVarint(string s, ref int index)
    {
        long result = 0;
        var shift = 0;
        int b;
        do
        {
            if (index >= s.Length) throw new FormatException("Truncated polyline.");
            b = s[index++] - 63;
            result |= (long)(b & 0x1f) << shift;
            shift += 5;
        } while (b >= 0x20);

        // ZigZag decode: low bit flags negative
        return (result & 1) != 0 ? ~(result >> 1) : (result >> 1);
    }

    private static void WriteVarint(StringBuilder sb, long value)
    {
        // ZigZag encode
        var v = value < 0 ? ~(value << 1) : (value << 1);
        while (v >= 0x20)
        {
            sb.Append((char)((0x20 | (int)(v & 0x1f)) + 63));
            v >>= 5;
        }
        sb.Append((char)((int)v + 63));
    }
}
