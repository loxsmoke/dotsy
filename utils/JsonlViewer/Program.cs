if (args.Length != 1 || args[0] is "-h" or "--help" or "/?")
{
    Console.Error.WriteLine("Usage: jsonl-viewer <file.jsonl>");
    return args.Length == 1 ? 0 : 2;
}

var path = Path.GetFullPath(args[0]);
if (!File.Exists(path))
{
    Console.Error.WriteLine($"File not found: {path}");
    return 1;
}

List<SourceLine> lines;
try
{
    lines = JsonlFormatter.Load(path);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

using var app = Application.Create().Init();
{
    var window = new Window
    {
        Title = Path.GetFileName(path),
        X = 0,
        Y = 0,
        Width = Dim.Fill(),
        Height = Dim.Fill()
    };

    var status = new Label
    {
        Text = "Arrows/PageUp/PageDown move  |  Shift+arrows selects  |  Ctrl+C copies  |  Esc quits",
        X = 1,
        Y = Pos.AnchorEnd(1),
        Width = Dim.Fill(2),
        Height = 1
    };

    var viewer = new JsonlView(lines, status, app)
    {
        X = 0,
        Y = 0,
        Width = Dim.Fill(),
        Height = Dim.Fill(1)
    };

    window.Add(viewer, status);
    viewer.SetFocus();
    app.Run(window);
}

return 0;
