namespace SubReel.Launcher.Application.Authentication;

public enum SignInStage
{
    RequestingCode,
    /// <summary>Ждём, пока пользователь введёт код на сайте Microsoft (<see cref="SignInProgress.Prompt"/> заполнен).</summary>
    WaitingForUser,
    SigningInXbox,
    SigningInXsts,
    SigningInMinecraft,
    CheckingOwnership,
    LoadingProfile,
    Done,
}

/// <param name="UserCode">Код, который вводит пользователь.</param>
/// <param name="VerificationUri">Адрес страницы, куда его вводят.</param>
/// <param name="ExpiresAt">Когда код перестанет действовать.</param>
public sealed record DeviceCodePrompt(string UserCode, string VerificationUri, DateTimeOffset ExpiresAt);

public sealed record SignInProgress(SignInStage Stage, DeviceCodePrompt? Prompt = null);
