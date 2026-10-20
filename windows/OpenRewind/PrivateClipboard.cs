using System.Drawing;

namespace OpenRewind;

/// Copies from the timeline, kept out of clipboard history (Win+V) and cloud clipboard sync, and away from
/// clipboard monitors that respect it. They still paste normally.
/// https://learn.microsoft.com/windows/win32/dataxchg/clipboard-formats#cloud-clipboard-and-clipboard-history-formats
static class PrivateClipboard
{
    public static void SetText(string text)
    {
        var data = Private();
        data.SetText(text, TextDataFormat.UnicodeText);
        Clipboard.SetDataObject(data, copy: true);
    }

    public static void SetImage(Image image)
    {
        var data = Private();
        data.SetImage(image);
        Clipboard.SetDataObject(data, copy: true);
    }

    static DataObject Private()
    {
        var data = new DataObject();
        static MemoryStream Dword(int v) => new(BitConverter.GetBytes(v));
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", Dword(0));
        data.SetData("CanIncludeInClipboardHistory", Dword(0));
        data.SetData("CanUploadToCloudClipboard", Dword(0));
        return data;
    }
}
