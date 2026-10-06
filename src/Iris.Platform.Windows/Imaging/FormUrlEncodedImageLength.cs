namespace Iris.Platform.Windows.Imaging;

internal static class FormUrlEncodedImageLength
{
    internal static long Count(ReadOnlySpan<byte> bytes, Action check)
    {
        long length = 4 * ((bytes.Length + 2L) / 3);
        int complete = bytes.Length - bytes.Length % 3;
        int offset = 0;
        while (offset < complete)
        {
            check();
            int end = offset + Math.Min(12_288, complete - offset);
            for (; offset < end; offset += 3)
            {
                int a = bytes[offset], b = bytes[offset + 1], c = bytes[offset + 2];
                if ((a >> 2) >= 62) length += 2;
                if ((((a & 3) << 4) | (b >> 4)) >= 62) length += 2;
                if ((((b & 15) << 2) | (c >> 6)) >= 62) length += 2;
                if ((c & 63) >= 62) length += 2;
            }
        }
        check();
        if (offset < bytes.Length)
        {
            int a = bytes[offset];
            if ((a >> 2) >= 62) length += 2;
            if (bytes.Length - offset == 2)
            {
                if ((((a & 3) << 4) | (bytes[offset + 1] >> 4)) >= 62) length += 2;
                length += 2; // One escaped '='; the last data sextet cannot be '+' or '/'.
            }
            else length += 4; // Two escaped '=' characters.
        }
        return length;
    }
}
