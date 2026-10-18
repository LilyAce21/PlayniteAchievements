namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Where achievement notifications appear. Saved numerically, so new values go at the end.
    /// </summary>
    public enum ToastScreenCorner
    {
        BottomRight,
        BottomLeft,
        TopRight,
        TopLeft,
        TopCenter,
        BottomCenter
    }

    public static class ToastScreenCornerExtensions
    {
        /// <summary>The position's placement along the anchor's horizontal axis.</summary>
        public static ToastHorizontalAlignment Horizontal(this ToastScreenCorner position)
        {
            switch (position)
            {
                case ToastScreenCorner.BottomLeft:
                case ToastScreenCorner.TopLeft:
                    return ToastHorizontalAlignment.Left;
                case ToastScreenCorner.TopCenter:
                case ToastScreenCorner.BottomCenter:
                    return ToastHorizontalAlignment.Center;
                default:
                    return ToastHorizontalAlignment.Right;
            }
        }

        /// <summary>Whether the position sits against the anchor's bottom edge.</summary>
        public static bool IsBottom(this ToastScreenCorner position)
        {
            switch (position)
            {
                case ToastScreenCorner.TopLeft:
                case ToastScreenCorner.TopCenter:
                case ToastScreenCorner.TopRight:
                    return false;
                default:
                    return true;
            }
        }
    }
}
