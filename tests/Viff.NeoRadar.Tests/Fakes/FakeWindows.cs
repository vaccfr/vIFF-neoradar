using System.Collections.Generic;
using NeoRadar.PluginSdk.Window;

namespace Viff.NeoRadar.Tests.Fakes;

internal sealed class FakeWindows : IWindowInterface
{
    public WindowHandle Handle { get; } = new(7);

    public WindowDefinition? Opened { get; private set; }

    public List<IReadOnlyList<WindowElement>> Replaced { get; } = new();

    public WindowHandle OpenWindow(WindowDefinition definition)
    {
        Opened = definition;
        return Handle;
    }

    public bool ReplaceChildren(WindowHandle window, string elementId, IReadOnlyList<WindowElement> children)
    {
        Replaced.Add(children);
        return true;
    }

    public bool CloseWindow(WindowHandle window) => true;

    public bool UpdateElement(WindowHandle window, string elementId, string text) => true;

    public bool SetElementVisible(WindowHandle window, string elementId, bool visible) => true;

    public bool SetElementEnabled(WindowHandle window, string elementId, bool enabled) => true;

    public bool SetElementStyle(WindowHandle window, string elementId, ElementStyle style) => true;

    public bool AppendChild(WindowHandle window, string parentId, WindowElement child) => true;

    public bool RemoveChild(WindowHandle window, string elementId) => true;

    public bool SetSelectedIndex(WindowHandle window, string elementId, int index) => true;

    public bool SetChecked(WindowHandle window, string elementId, bool @checked) => true;

    public bool SetOptions(WindowHandle window, string elementId, IReadOnlyList<string> options, int selectedIndex) => true;

    public bool SetTableRows(WindowHandle window, string elementId, IReadOnlyList<TableRow> rows, int selectedIndex) => true;

    public bool SetTableSelection(WindowHandle window, string elementId, int index) => true;
}
