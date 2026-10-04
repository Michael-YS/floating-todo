using FloatingTodo;
using System.Windows;
using System.Windows.Controls;

internal static class EditorChecks
{
    public static void Run(Action<bool, string> check)
    {
        T Control<T>(EditorWindow editor, string name) where T : FrameworkElement => (T)editor.FindName(name);
        TodoItem Task(string rule) => new() { Title = "Editor recurrence", Due = new DateTime(2026, 10, 26), RecurrenceRule = rule };
        var lists = new[] { new TodoListInfo { Id = "personal", Name = "Local", Color = "#FFFFFF", IsLocal = true } };
        var editor = new EditorWindow(Task("FREQ=MONTHLY;BYDAY=MO;BYSETPOS=-1;COUNT=3"), lists);
        check(Control<ComboBox>(editor, "RecurrencePresetBox").SelectedValue as string == "__VISUAL__", "supported existing rule did not select visual editor");
        check(Control<ComboBox>(editor, "PositionBox").SelectedIndex == 5, "monthly last weekday did not load in editor");
        Control<TextBox>(editor, "CountBox").Text = "4";
        Control<ComboBox>(editor, "RecurrencePresetBox").SelectedValue = "__CUSTOM__";
        check(Control<TextBox>(editor, "RecurrenceBox").Text.EndsWith("COUNT=4", StringComparison.Ordinal), "switch to raw mode lost visual edits");
        Control<ComboBox>(editor, "RecurrencePresetBox").SelectedValue = "__VISUAL__";
        TodoItem? saved = null;
        editor.TaskSaved += task => saved = task;
        editor.Close();
        check(saved?.RecurrenceRule == "FREQ=MONTHLY;BYDAY=MO;BYSETPOS=-1;COUNT=4", "editor close did not save configuration");

        const string complex = "FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU";
        var rawEditor = new EditorWindow(Task(complex), lists);
        check(Control<ComboBox>(rawEditor, "RecurrencePresetBox").SelectedValue as string == "__CUSTOM__", "complex existing rule did not select raw editor");
        saved = null;
        rawEditor.TaskSaved += task => saved = task;
        rawEditor.Close();
        check(saved is null, "unchanged complex RRULE triggered a save");
        rawEditor = new EditorWindow(Task(complex), lists);
        Control<TextBox>(rawEditor, "TitleBox").Text = "Changed title";
        rawEditor.TaskSaved += task => saved = task;
        rawEditor.Close();
        check(saved?.RecurrenceRule == complex, "title edit changed unsupported existing RRULE");

        foreach (var scenario in new[] { "unchanged", "changed", "restored" })
        {
            var visualEditor = new EditorWindow(Task("FREQ=MONTHLY;BYDAY=MO;BYSETPOS=-1;COUNT=3"), lists);
            var saves = 0;
            visualEditor.TaskSaved += _ => saves++;
            if (scenario != "unchanged") Control<TextBox>(visualEditor, "CountBox").Text = "4";
            if (scenario == "restored") Control<TextBox>(visualEditor, "CountBox").Text = "3";
            visualEditor.Close();
            check(saves == (scenario == "changed" ? 1 : 0), "visual configuration change detection: " + scenario);
        }

        var invalidEditor = new EditorWindow(Task("FREQ=WEEKLY;INTERVAL=2"), lists);
        Control<TextBox>(invalidEditor, "IntervalBox").Text = "0";
        Control<ComboBox>(invalidEditor, "RecurrencePresetBox").SelectedValue = "__CUSTOM__";
        check(Control<ComboBox>(invalidEditor, "RecurrencePresetBox").SelectedValue as string == "__VISUAL__" &&
              Control<TextBox>(invalidEditor, "IntervalBox").Text == "0", "invalid visual edits were silently discarded during mode switch");
        saved = null;
        invalidEditor.TaskSaved += task => saved = task;
        invalidEditor.Close();
        check(saved is null && Control<TextBlock>(invalidEditor, "ValidationText").Text.Length > 0, "invalid configuration did not block editor save");
        Control<TextBox>(invalidEditor, "IntervalBox").Text = "3";
        invalidEditor.Close();
        check(saved?.RecurrenceRule == "FREQ=WEEKLY;INTERVAL=3", "corrected visual configuration did not save");
    }
}
