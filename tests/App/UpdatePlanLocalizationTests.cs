using ResourceManager.Shared.Localization;
using ResourceManager.Updater;

namespace Resource_Manager_APP.Tests;

public sealed class UpdatePlanLocalizationTests
{
    [Theory]
    [InlineData("0.2.8", "0.2.7", false, "update.versionNotNewer")]
    [InlineData("0.2.8", "0.2.8", false, "update.versionNotNewer")]
    [InlineData("0.2.8", "0.2.9", true, "update.repairVersionMismatch")]
    public void RejectedVersionHasStableCodeAndTranslatedParameters(
        string installed, string target, bool repair, string expectedCode)
    {
        var exception = Assert.Throws<UpdatePlanException>(() => UpdatePlan.RequireVersion(installed, target, repair ? UpdateOperation.Repair : UpdateOperation.Update));
        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal([installed, target], exception.Parameters);
        var english = UpdateManagerCommand.FormatFailure(exception, ToolText.For("en-US"));
        foreach (var language in AppLanguage.SupportedIds)
        {
            var message = UpdateManagerCommand.FormatFailure(exception, ToolText.For(language));
            Assert.Contains(installed, message);
            Assert.Contains(target, message);
            Assert.DoesNotContain(expectedCode, message);
            if (language != "en-US") Assert.NotEqual(english, message);
        }
        Assert.Contains(expectedCode, exception.ToString());
    }

    [Fact]
    public void AcceptedVersionRulesAreUnchanged()
    {
        UpdatePlan.RequireVersion("0.2.8", "0.2.9", UpdateOperation.Update);
        UpdatePlan.RequireVersion("0.2.8", "0.2.8", UpdateOperation.Repair);
    }

    [Fact]
    public void UnexpectedFailureDetailsStayOutOfDisplayCopy()
    {
        var exception = new IOException("PRIVATE-DIAGNOSTIC-TOKEN");
        foreach (var language in AppLanguage.SupportedIds)
            Assert.Equal(ToolText.For(language).OperationFailed,
                UpdateManagerCommand.FormatFailure(exception, ToolText.For(language)));
    }
}
