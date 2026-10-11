using Capture.Core.Models;
using Capture.Core.Profiles;
using Capture.Core.Scripting;
using Capture.Core.Watch;
using Xunit;

namespace Capture.Scripting.Tests;

public class ScriptDialogTests
{
    private sealed class Settings : IWatchSettingsStore
    {
        public Task<WatchSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WatchSettings());
        public Task SaveAsync(WatchSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeDialogs(bool answer, TimeSpan? delay = null) : IScriptDialogs
    {
        public List<string> Shown { get; } = [];

        public async Task MessageAsync(string title, string message)
        {
            Shown.Add($"{title}: {message}");
            if (delay is { } wait)
                await Task.Delay(wait);
        }

        public Task<bool> ConfirmAsync(string title, string question)
        {
            Shown.Add($"{title}? {question}");
            return Task.FromResult(answer);
        }
    }

    private static ScriptExecutionContext Context(IScriptDialogs? dialogs, params IndexValue[] values) => new()
    {
        ProfileName = "Test",
        DocumentNumber = 1,
        BatchNumber = 1,
        Timestamp = DateTimeOffset.UtcNow,
        Values = values,
        Document = new ScriptDocumentInfo { FileName = "a.pdf", FileExtension = ".pdf", PageCount = 1, Text = "" },
        Dialogs = dialogs
    };

    [Theory]
    [InlineData(true, "Replaced")]
    [InlineData(false, "Kept")]
    public async Task Confirm_returns_the_answer_and_message_shows_with_the_button_name(bool answer, string expected)
    {
        var dialogs = new FakeDialogs(answer);
        var supplier = new IndexValue { FieldId = Guid.NewGuid(), FieldName = "Supplier", Value = "Old" };
        var script = new FieldScript
        {
            Name = "Check supplier",
            Source = """
                if (await Confirm("Replace the supplier name?"))
                    Fields["Supplier"].Value = "Replaced";
                else
                    Fields["Supplier"].Value = "Kept";
                await Message("Done", "Supplier check");
                """
        };

        var result = await new RoslynFieldScriptRunner(new Settings(), new HttpClient()).RunProfileScriptAsync(script, Context(dialogs, supplier));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(expected, supplier.Value);
        Assert.Equal(["Check supplier? Replace the supplier name?", "Supplier check: Done"], dialogs.Shown);
    }

    [Fact]
    public async Task Without_someone_to_answer_a_dialog_fails_the_script_with_a_reason()
    {
        var result = await new RoslynFieldScriptRunner(new Settings(), new HttpClient())
            .RunProfileScriptAsync(new FieldScript { Source = """await Message("Hello");""" }, Context(null));

        Assert.False(result.Success);
        Assert.Contains("only works in a Button field's script", result.ErrorMessage);
    }

    [Fact]
    public async Task Time_spent_reading_a_message_does_not_count_toward_the_timeout()
    {
        var dialogs = new FakeDialogs(true, delay: TimeSpan.FromSeconds(3.5));
        var script = new FieldScript { TimeoutSeconds = 3, Source = """await Message("Take your time");""" };

        var result = await new RoslynFieldScriptRunner(new Settings(), new HttpClient()).RunProfileScriptAsync(script, Context(dialogs));

        Assert.True(result.Success, result.ErrorMessage);
    }
}
