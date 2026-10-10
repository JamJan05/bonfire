namespace Bonfire.App.Services;

public interface IDialogService
{
    /// <summary>Asks for confirmation; Cancel is the default. Returns true only on explicit confirm.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool dangerous);
}
