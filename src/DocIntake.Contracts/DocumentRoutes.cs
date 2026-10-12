namespace DocIntake.Contracts;

public static class DocumentRoutes
{
    public const string Form = "form-agent";
    public const string Vision = "vision-agent";
    public const string Text = "text-agent";
    public const string Spreadsheet = "spreadsheet-agent";

    public static readonly string[] All = [Form, Vision, Text, Spreadsheet];
}
