using System.Text.Json.Serialization;

namespace SubReel.Launcher.Domain.Accounts;

// ───────────────────────── Кастомизация профиля ─────────────────────────
//
// Всё, что игрок сможет менять в оформлении профиля, описано одной моделью ProfileCosmetics и хранится
// в аккаунте. Позже эти поля может отдавать сервер SubReel ID (и проверять права на платные/особые стили) —
// отрисовка от этого не изменится: CosmeticsRenderer просто читает модель.

/// <summary>Как красится ник или рамка.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ColorMode
{
    /// <summary>Стиль по умолчанию (белый ник / обычная рамка).</summary>
    None,
    /// <summary>Один цвет (ColorA).</summary>
    Solid,
    /// <summary>Градиент ColorA → ColorB (при Animated переливается).</summary>
    Gradient,
    /// <summary>Радуга (при Animated переливается).</summary>
    Rainbow,
}

/// <summary>Что показывать в слоте аватарки.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AvatarKind
{
    /// <summary>Две буквы ника на цветном фоне.</summary>
    Initials,
    /// <summary>Голова скина по нику.</summary>
    SkinHead,
    /// <summary>Своя картинка с диска (AvatarImagePath).</summary>
    Image,
}

public sealed class ProfileCosmetics
{
    // Ник
    public ColorMode NickMode { get; set; } = ColorMode.None;
    public string NickColorA { get; set; } = "#FFFFFF";
    public string NickColorB { get; set; } = "#78C8FF";
    public bool NickAnimated { get; set; }

    // Рамка кнопки профиля
    public ColorMode FrameMode { get; set; } = ColorMode.None;
    public string FrameColorA { get; set; } = "#4C85FF";
    public string FrameColorB { get; set; } = "#78C8FF";
    public bool FrameAnimated { get; set; }
    public bool FrameGlow { get; set; }

    // Аватарка
    public AvatarKind Avatar { get; set; } = AvatarKind.SkinHead;
    public string? AvatarImagePath { get; set; }
}
