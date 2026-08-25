using SkeldForge.Models;

namespace SkeldForge.ViewModels.Infrastructure;

public static class ModpackWorkflows
{
    public static async Task<ModpackInfo?> CreateAsync(MainViewModel main)
    {
        if (main.GameInstallation is null)
        {
            await main.Root.Dialogs.ShowErrorAsync(main.Text["Dialog.ErrorTitle"], main.Text["Error.GameMissing"]);
            return null;
        }

        var name = await main.Root.Dialogs.PromptAsync(
            main.Text["Modpack.CreateTitle"],
            main.Text["Modpack.NamePrompt"],
            main.Text["Modpack.DefaultName"]);
        if (string.IsNullOrWhiteSpace(name)) return null;

        var result = await main.RunBusyAsync("Progress.Copying", progress =>
            main.Root.CreateModpackAsync(name, main.GameInstallation.Path, progress));
        if (!result.Success || result.Value is null)
        {
            await main.ShowFailureAsync(result);
            return null;
        }

        await main.RefreshCollectionsAsync();
        main.ShowStatus(main.Text.Format("Success.ModpackCreated", result.Value.Name));
        main.ShowModpack(result.Value);
        return result.Value;
    }

    public static async Task ImportAsync(MainViewModel main)
    {
        if (main.GameInstallation is null)
        {
            await main.Root.Dialogs.ShowErrorAsync(main.Text["Dialog.ErrorTitle"], main.Text["Error.GameMissing"]);
            return;
        }

        var path = main.Root.Dialogs.PickSkeldpack();
        if (path is null) return;
        var previewResult = await main.Root.PreviewSkeldpackAsync(path);
        if (!previewResult.Success || previewResult.Value is null)
        {
            await main.ShowFailureAsync(previewResult);
            return;
        }
        if (!await main.Root.Dialogs.ReviewImportAsync(previewResult.Value)) return;

        var result = await main.RunBusyAsync("Progress.Importing", progress =>
            main.Root.ImportSkeldpackAsync(path, main.GameInstallation.Path, progress));
        if (!result.Success || result.Value is null)
        {
            await main.ShowFailureAsync(result);
            return;
        }
        await main.RefreshCollectionsAsync();
        main.ShowStatus(main.Text["Success.Imported"]);
        main.ShowModpack(result.Value);
    }
}
