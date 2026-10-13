namespace eft_dma_radar.Arena.UI
{
    /// <summary>
    /// Provides a platform font without redistributing a font binary.
    /// </summary>
    internal static class CustomFonts
    {
        public static SKTypeface Regular { get; } = LoadFont();

        /// <summary>The public snapshot uses ImGui's built-in font.</summary>
        internal static byte[]? GetEmbeddedFontData()
        {
            return null;
        }

        private static SKTypeface LoadFont()
        {
            return SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
        }
    }
}
