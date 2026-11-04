using System.Runtime;
using System.Windows;
using System.Windows.Threading;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application;
using SubReel.Launcher.Application.Abstractions;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Authentication;
using SubReel.Launcher.Application.Java;
using SubReel.Launcher.Application.News;
using SubReel.Launcher.Application.Updates;
using SubReel.Launcher.Design.DependencyInjection;
using SubReel.Launcher.Design.Rendering;
using SubReel.Launcher.Design.Services;
using SubReel.Launcher.Design.Views;
using SubReel.Launcher.Infrastructure;
using SubReel.Launcher.Infrastructure.Images;
using SubReel.Launcher.Infrastructure.Logging;
using SubReel.Launcher.Infrastructure.Storage;
using SubReel.Launcher.Minecraft;
using SubReel.Launcher.Mods;

namespace SubReel.Launcher.Design;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private ILogger<App>? _logger;

    /// <summary>
    /// Корень композиции: единственное место, где собирается граф зависимостей. Регистрация разбита по слоям
    /// (Infrastructure → Application → Presentation), а окно получает готовую MainViewModel из контейнера.
    /// Обработчики необработанных исключений ставятся до сборки хоста, чтобы любой сбой запуска остался в журнале.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterGlobalExceptionLogging();

        try
        {
            // Папка данных определяется и (если пользователь её сменил или это первый запуск после обновления) переносится
            // до создания журнала и любых файлов данных. Что было сделано, пишем в журнал чуть ниже.
            var location = DataLocationService.StartDefault();
            var paths = new AppPaths(location.CurrentRoot);
            StartJitProfile(paths);
            // Корень содержимого — папка с exe, а не текущая папка процесса: иначе appsettings.json не находится,
            // если лаунчер запущен ярлыком с другой рабочей папкой.
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = e.Args,
                ContentRootPath = AppContext.BaseDirectory,
            });
            // Консоли у WPF-приложения нет: сообщения «Press Ctrl+C to shut down» в журнале только путают.
            builder.Services.Configure<ConsoleLifetimeOptions>(options => options.SuppressStatusMessages = true);

            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Information);
            builder.Services.AddSingleton<ILoggerProvider>(_ => new FileLoggerProvider(paths.LogsDirectory));

            builder.Services.AddOptions<NewsOptions>().BindConfiguration(NewsOptions.SectionName);
            builder.Services.AddOptions<UpdateOptions>().BindConfiguration(UpdateOptions.SectionName);
            builder.Services.AddOptions<DownloadOptions>().BindConfiguration(DownloadOptions.SectionName);
            builder.Services.AddOptions<JavaOptions>().BindConfiguration(JavaOptions.SectionName);
            builder.Services.AddOptions<AuthenticationOptions>().BindConfiguration(AuthenticationOptions.SectionName);
            builder.Services.AddSingleton<IDataLocationService>(location);
            builder.Services
                .AddSubReelInfrastructure(paths)
                .AddSubReelApplication()
                .AddSubReelMinecraft()
                .AddSubReelMods()
                .AddSubReelAuthentication()
                .AddSubReelGameLauncher()
                .AddSubReelPresentation();

            _host = builder.Build();
            _logger = _host.Services.GetRequiredService<ILogger<App>>();
            _logger.LogInformation("SubReel Launcher запускается. Папка данных: {Root}.", paths.Root);
            foreach (var note in location.StartupNotes) _logger.LogInformation("Папка данных: {Note}", note);

            _host.Start();
            RenderingServices.Initialize(_host.Services.GetRequiredService<IImageService>(), _logger);

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();

            // Через полминуты после старта (когда окно уже работает) подчищаем кэш картинок: старые файлы удаляются, если он вырос больше лимита.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                ImageCachePruner.Prune(paths.ImageCacheDirectory);
            });
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "Не удалось запустить SubReel Launcher.");
            MessageBox.Show($"Не удалось запустить SubReel Launcher:\n{ex.Message}", "SubReel Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Многоядерная JIT-компиляция: при запуске записывается, какие методы нужны, и со следующего запуска они компилируются заранее в фоне.
    /// Окно открывается быстрее; всё необязательное — не получилось создать папку, запускаемся как обычно.
    /// </summary>
    private static void StartJitProfile(AppPaths paths)
    {
        try
        {
            var folder = Path.Combine(paths.CacheDirectory, "jit");
            Directory.CreateDirectory(folder);
            ProfileOptimization.SetProfileRoot(folder);
            ProfileOptimization.StartProfile("launcher.jitprofile");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // не критично
        }
    }

    /// <summary>Останавливает хост: контейнер освобождает все сервисы (в том числе MainViewModel и журнал).</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("SubReel Launcher завершает работу (код {ExitCode}).", e.ApplicationExitCode);

        if (_host is not null)
        {
            try
            {
                _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Ошибка при остановке хоста.");
            }
            _host.Dispose();
        }

        base.OnExit(e);
    }

    /// <summary>Любое необработанное исключение попадает в журнал. Поведение приложения не меняется: UI-исключения по-прежнему завершают процесс.</summary>
    private void RegisterGlobalExceptionLogging()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger?.LogCritical(args.ExceptionObject as Exception, "Необработанное исключение (завершение: {IsTerminating}).", args.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.LogError(args.Exception, "Необработанное исключение в фоновой задаче.");
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) =>
        _logger?.LogCritical(e.Exception, "Необработанное исключение в потоке интерфейса.");
}
