namespace FeatherBrowser.Core;

internal static class BrowserShortcuts
{
    // Chromium 和 WinForms 使用同一张表；保留网页输入框的复制、粘贴、全选等按键。
    internal static bool Contains(Keys keys)
    {
        Keys code = keys & Keys.KeyCode;
        Keys modifiers = keys & Keys.Modifiers;
        if (modifiers == Keys.Control)
            return code is Keys.T or Keys.W or Keys.N or Keys.L or Keys.D or Keys.F or Keys.R
                or Keys.Oemcomma or Keys.Tab or Keys.PageDown or Keys.PageUp or Keys.Oemplus
                or Keys.OemMinus or Keys.Add or Keys.Subtract or Keys.D0 or Keys.NumPad0
                or Keys.J or Keys.H || (code >= Keys.D1 && code <= Keys.D9);
        if (modifiers == (Keys.Control | Keys.Shift))
            return code is Keys.T or Keys.N or Keys.E or Keys.O or Keys.C or Keys.Tab or Keys.R or Keys.Oemplus;
        if (modifiers == Keys.Alt) return code is Keys.Left or Keys.Right or Keys.D or Keys.F;
        return modifiers == Keys.None && code is Keys.F5 or Keys.F6 or Keys.F11 or Keys.Escape;
    }
}
