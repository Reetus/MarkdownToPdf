using System.Buffers.Binary;
using System.IO.Compression;

namespace MarkdownToPdf.Images;

/// <summary>Image data ready to be written as a PDF image XObject.</summary>
internal sealed class PdfImage
{
    public int Width { get; init; }
    public int Height { get; init; }
    public string ColorSpace { get; init; } = "DeviceRGB";
    public int BitsPerComponent { get; init; } = 8;
    public required byte[] Data { get; init; }
    public string Filter { get; init; } = "FlateDecode";
    /// <summary>Flate-compressed 8-bit alpha channel, or null.</summary>
    public byte[]? SoftMask { get; init; }
    public bool InvertedCmyk { get; init; }
    public string ResourceName { get; set; } = "";
    public int ObjectNumber { get; set; }

    /// <summary>Natural size in points (96 dpi assumed for pixels).</summary>
    public float NaturalWidth => Width * 0.75f;
    public float NaturalHeight => Height * 0.75f;
}

internal static class ImageDecoder
{
    public static PdfImage Decode( byte[] data )
    {
        if ( data.Length > 8 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G' )
        {
            return DecodePng( data );
        }

        if ( data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 )
        {
            return DecodeJpeg( data );
        }

        if ( data.Length > 2 && data[0] == 'B' && data[1] == 'M' )
        {
            return DecodeBmp( data );
        }

        throw new NotSupportedException( "Unsupported image format (PNG, JPEG and BMP are supported)." );
    }

    // ───────────────────────────── JPEG ─────────────────────────────

    private static PdfImage DecodeJpeg( byte[] d )
    {
        var i = 2;
        var adobe = false;
        while ( i + 4 < d.Length )
        {
            if ( d[i] != 0xFF ) { i++; continue; }
            var marker = d[i + 1];
            if ( marker == 0xFF ) { i++; continue; }
            if ( marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7 ) { i += 2; continue; }
            var len = ( d[i + 2] << 8 ) | d[i + 3];
            if ( marker == 0xEE && len >= 12 && d[i + 4] == 'A' && d[i + 5] == 'd' && d[i + 6] == 'o' && d[i + 7] == 'b' && d[i + 8] == 'e' )
            {
                adobe = true;
            }

            if ( marker is >= 0xC0 and <= 0xCF && marker is not ( 0xC4 or 0xC8 or 0xCC ) )
            {
                int bpc = d[i + 4];
                var h = ( d[i + 5] << 8 ) | d[i + 6];
                var w = ( d[i + 7] << 8 ) | d[i + 8];
                int comps = d[i + 9];
                return new PdfImage
                {
                    Width = w,
                    Height = h,
                    BitsPerComponent = bpc,
                    Data = d,
                    Filter = "DCTDecode",
                    ColorSpace = comps switch { 1 => "DeviceGray", 4 => "DeviceCMYK", _ => "DeviceRGB" },
                    InvertedCmyk = comps == 4 && adobe,
                };
            }
            i += 2 + len;
        }
        throw new InvalidDataException( "JPEG has no frame header." );
    }

    // ───────────────────────────── PNG ─────────────────────────────

    private static PdfImage DecodePng( byte[] d )
    {
        var pos = 8;
        int w = 0, h = 0, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null, trns = null;
        var idat = new MemoryStream();
        while ( pos + 8 <= d.Length )
        {
            var len = (int) BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( pos ) );
            var type = System.Text.Encoding.ASCII.GetString( d, pos + 4, 4 );
            var chunk = d.AsSpan( pos + 8, len );
            switch ( type )
            {
                case "IHDR":
                    w = (int) BinaryPrimitives.ReadUInt32BigEndian( chunk );
                    h = (int) BinaryPrimitives.ReadUInt32BigEndian( chunk[4..] );
                    bitDepth = chunk[8]; colorType = chunk[9]; interlace = chunk[12];
                    break;
                case "PLTE": palette = chunk.ToArray(); break;
                case "tRNS": trns = chunk.ToArray(); break;
                case "IDAT": idat.Write( chunk ); break;
            }
            if ( type == "IEND" )
            {
                break;
            }

            pos += 12 + len;
        }
        if ( w <= 0 || h <= 0 )
        {
            throw new InvalidDataException( "Invalid PNG header." );
        }

        idat.Position = 0;
        using var z = new ZLibStream( idat, CompressionMode.Decompress );
        var raw = new MemoryStream();
        z.CopyTo( raw );
        var inflated = raw.ToArray();

        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException( "Bad PNG color type." ) };
        // Decode to 8-bit samples (palette indices kept as indices)
        var samples = new byte[w * h * channels];
        if ( interlace == 0 )
        {
            Unfilter( inflated, 0, w, h, channels, bitDepth, colorType, samples, 0, 0, 1, 1, w );
        }
        else
        {
            int[] sx = [0, 4, 0, 2, 0, 1, 0], sy = [0, 0, 4, 0, 2, 0, 1], dx = [8, 8, 4, 4, 2, 2, 1], dy = [8, 8, 8, 4, 4, 2, 2];
            var offset = 0;
            for ( var p = 0; p < 7; p++ )
            {
                var pw = ( w - sx[p] + dx[p] - 1 ) / dx[p];
                var ph = ( h - sy[p] + dy[p] - 1 ) / dy[p];
                if ( pw <= 0 || ph <= 0 )
                {
                    continue;
                }

                offset = Unfilter( inflated, offset, pw, ph, channels, bitDepth, colorType, samples, sx[p], sy[p], dx[p], dy[p], w );
            }
        }

        var gray = colorType is 0 or 4;
        var outCh = gray ? 1 : 3;
        var color = new byte[w * h * outCh];
        byte[]? alpha = null;
        var hasAlpha = colorType is 4 or 6 || trns != null;
        if ( hasAlpha )
        {
            alpha = new byte[w * h];
        }

        var maxVal = ( 1 << Math.Min( bitDepth, 8 ) ) - 1;
        for ( var px = 0; px < w * h; px++ )
        {
            switch ( colorType )
            {
                case 0:
                    {
                        var g = samples[px];
                        color[px] = g;
                        if ( alpha != null )
                        {
                            alpha[px] = trns!.Length >= 2 && ScaleToDepth( g, bitDepth ) == ( ( trns[0] << 8 ) | trns[1] ) ? (byte) 0 : (byte) 255;
                        }

                        break;
                    }
                case 2:
                    {
                        byte r = samples[px * 3], g = samples[px * 3 + 1], b = samples[px * 3 + 2];
                        color[px * 3] = r; color[px * 3 + 1] = g; color[px * 3 + 2] = b;
                        if ( alpha != null )
                        {
                            alpha[px] = trns!.Length >= 6 && r == trns[1] && g == trns[3] && b == trns[5] ? (byte) 0 : (byte) 255;
                        }

                        break;
                    }
                case 3:
                    {
                        int idx = samples[px];
                        if ( palette != null && idx * 3 + 2 < palette.Length )
                        {
                            color[px * 3] = palette[idx * 3]; color[px * 3 + 1] = palette[idx * 3 + 1]; color[px * 3 + 2] = palette[idx * 3 + 2];
                        }
                        if ( alpha != null )
                        {
                            alpha[px] = idx < trns!.Length ? trns[idx] : (byte) 255;
                        }

                        break;
                    }
                case 4:
                    color[px] = samples[px * 2];
                    alpha![px] = samples[px * 2 + 1];
                    break;
                case 6:
                    color[px * 3] = samples[px * 4]; color[px * 3 + 1] = samples[px * 4 + 1]; color[px * 3 + 2] = samples[px * 4 + 2];
                    alpha![px] = samples[px * 4 + 3];
                    break;
            }
        }
        _ = maxVal;
        if ( alpha != null && alpha.All( a => a == 255 ) )
        {
            alpha = null;
        }

        return new PdfImage
        {
            Width = w,
            Height = h,
            ColorSpace = gray ? "DeviceGray" : "DeviceRGB",
            BitsPerComponent = 8,
            Data = Compress( color ),
            SoftMask = alpha == null ? null : Compress( alpha ),
        };
    }

    private static int ScaleToDepth( byte scaled8, int bitDepth ) =>
        bitDepth >= 8 ? scaled8 : scaled8 * ( ( 1 << bitDepth ) - 1 ) / 255; // inverse of the 8-bit expansion

    /// <summary>Reverses PNG scanline filters for one (sub)image and stores 8-bit samples into the destination grid.</summary>
    private static int Unfilter( byte[] src, int offset, int w, int h, int channels, int bitDepth, int colorType,
                                byte[] dest, int x0, int y0, int dxStep, int dyStep, int fullWidth )
    {
        var bitsPerPixel = channels * bitDepth;
        var stride = ( w * bitsPerPixel + 7 ) / 8;
        var bpp = Math.Max( 1, bitsPerPixel / 8 );
        var prev = new byte[stride];
        var cur = new byte[stride];
        for ( var y = 0; y < h; y++ )
        {
            if ( offset + 1 + stride > src.Length )
            {
                break;
            }

            int filter = src[offset];
            Array.Copy( src, offset + 1, cur, 0, stride );
            offset += 1 + stride;
            for ( var i = 0; i < stride; i++ )
            {
                var a = i >= bpp ? cur[i - bpp] : 0;
                int b = prev[i];
                var c = i >= bpp ? prev[i - bpp] : 0;
                int v = cur[i];
                cur[i] = (byte) ( filter switch
                {
                    1 => v + a,
                    2 => v + b,
                    3 => v + ( ( a + b ) >> 1 ),
                    4 => v + Paeth( a, b, c ),
                    _ => v,
                } );
            }
            var destY = y0 + y * dyStep;
            for ( var x = 0; x < w; x++ )
            {
                var destX = x0 + x * dxStep;
                var di = ( destY * fullWidth + destX ) * channels;
                for ( var ch = 0; ch < channels; ch++ )
                {
                    var sampleIndex = x * channels + ch;
                    byte value;
                    if ( bitDepth == 8 )
                    {
                        value = cur[sampleIndex];
                    }
                    else if ( bitDepth == 16 )
                    {
                        value = cur[sampleIndex * 2];
                    }
                    else
                    {
                        var bitPos = sampleIndex * bitDepth;
                        var raw = ( cur[bitPos >> 3] >> ( 8 - bitDepth - ( bitPos & 7 ) ) ) & ( ( 1 << bitDepth ) - 1 );
                        value = colorType == 3 ? (byte) raw : (byte) ( raw * 255 / ( ( 1 << bitDepth ) - 1 ) );
                    }
                    dest[di + ch] = value;
                }
            }
            (prev, cur) = (cur, prev);
        }
        return offset;
    }

    private static int Paeth( int a, int b, int c )
    {
        var p = a + b - c;
        int pa = Math.Abs( p - a ), pb = Math.Abs( p - b ), pc = Math.Abs( p - c );
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    // ───────────────────────────── BMP ─────────────────────────────

    private static PdfImage DecodeBmp( byte[] d )
    {
        var dataOffset = BinaryPrimitives.ReadInt32LittleEndian( d.AsSpan( 10 ) );
        var w = BinaryPrimitives.ReadInt32LittleEndian( d.AsSpan( 18 ) );
        var hRaw = BinaryPrimitives.ReadInt32LittleEndian( d.AsSpan( 22 ) );
        int bpp = BinaryPrimitives.ReadUInt16LittleEndian( d.AsSpan( 28 ) );
        var compression = BinaryPrimitives.ReadInt32LittleEndian( d.AsSpan( 30 ) );
        if ( ( bpp != 24 && bpp != 32 ) || ( compression != 0 && compression != 3 ) )
        {
            throw new NotSupportedException( "Only uncompressed 24/32-bit BMP files are supported." );
        }

        var h = Math.Abs( hRaw );
        var bottomUp = hRaw > 0;
        var stride = ( w * bpp / 8 + 3 ) & ~3;
        var rgb = new byte[w * h * 3];
        var alpha = bpp == 32 ? new byte[w * h] : null;
        for ( var y = 0; y < h; y++ )
        {
            var srcRow = dataOffset + ( bottomUp ? h - 1 - y : y ) * stride;
            for ( var x = 0; x < w; x++ )
            {
                var s = srcRow + x * ( bpp / 8 );
                var di = ( y * w + x ) * 3;
                rgb[di] = d[s + 2]; rgb[di + 1] = d[s + 1]; rgb[di + 2] = d[s];
                if ( alpha != null )
                {
                    alpha[y * w + x] = d[s + 3];
                }
            }
        }
        if ( alpha != null && ( alpha.All( a => a == 255 ) || alpha.All( a => a == 0 ) ) )
        {
            alpha = null;
        }

        return new PdfImage { Width = w, Height = h, Data = Compress( rgb ), SoftMask = alpha == null ? null : Compress( alpha ) };
    }

    public static byte[] Compress( byte[] data )
    {
        var ms = new MemoryStream();
        using ( var z = new ZLibStream( ms, CompressionLevel.Optimal, leaveOpen: true ) )
        {
            z.Write( data );
        }

        return ms.ToArray();
    }
}
