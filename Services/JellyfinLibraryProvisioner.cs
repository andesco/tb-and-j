using Jellyfin.Api.Controllers;
using Jellyfin.Api.Models.LibraryStructureDto;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorBoxSync.Services;

public sealed class JellyfinLibraryProvisioner
{
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILogger<JellyfinLibraryProvisioner> _logger;

    public JellyfinLibraryProvisioner(
        IServerConfigurationManager serverConfigurationManager,
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        ILogger<JellyfinLibraryProvisioner> logger)
    {
        _serverConfigurationManager = serverConfigurationManager;
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _logger = logger;
    }

    public async Task EnsureLibrariesAsync(PluginConfiguration configuration, bool refreshLibrary, CancellationToken cancellationToken)
    {
        if (!configuration.AutoCreateJellyfinLibraries)
        {
            return;
        }

        var rootPath = Path.GetFullPath(configuration.LibraryRootPath);
        var moviePath = Path.Combine(rootPath, "movies");
        var seriesPath = Path.Combine(rootPath, "series");

        Directory.CreateDirectory(moviePath);
        Directory.CreateDirectory(seriesPath);

        await EnsureLibraryAsync(
            configuration.MovieLibraryName,
            moviePath,
            CollectionTypeOptions.movies,
            refreshLibrary,
            cancellationToken).ConfigureAwait(false);

        await EnsureLibraryAsync(
            configuration.SeriesLibraryName,
            seriesPath,
            CollectionTypeOptions.tvshows,
            refreshLibrary,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureLibraryAsync(
        string libraryName,
        string path,
        CollectionTypeOptions collectionType,
        bool refreshLibrary,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(libraryName))
        {
            _logger.LogWarning("Skipping Jellyfin library provisioning because a library name is blank.");
            return;
        }

        var normalizedPath = StrmPathBuilder.NormalizePath(path);
        var virtualFolders = _libraryManager.GetVirtualFolders().ToList();
        var existingWithPath = virtualFolders.FirstOrDefault(folder => HasLocation(folder, normalizedPath));
        if (existingWithPath is not null)
        {
            _logger.LogDebug(
                "Jellyfin library path {Path} is already registered in library {LibraryName}.",
                normalizedPath,
                existingWithPath.Name);
            return;
        }

        var existingByName = virtualFolders.FirstOrDefault(folder =>
            string.Equals(folder.Name, libraryName, StringComparison.OrdinalIgnoreCase));

        if (existingByName is not null)
        {
            AddPathToExistingLibrary(libraryName, normalizedPath, refreshLibrary);
            _logger.LogInformation("Added TorBox path {Path} to existing Jellyfin library {LibraryName}.", normalizedPath, libraryName);
            return;
        }

        var controller = CreateController();
        var dto = new AddVirtualFolderDto
        {
            LibraryOptions = CreateLibraryOptions(normalizedPath)
        };

        await controller.AddVirtualFolder(
            libraryName,
            collectionType,
            [normalizedPath],
            dto,
            refreshLibrary).ConfigureAwait(false);

        _logger.LogInformation(
            "Created Jellyfin library {LibraryName} for TorBox path {Path}.",
            libraryName,
            normalizedPath);
    }

    private void AddPathToExistingLibrary(string libraryName, string path, bool refreshLibrary)
    {
        var controller = CreateController();
        controller.AddMediaPath(
            new MediaPathDto
            {
                Name = libraryName,
                Path = path,
                PathInfo = new MediaPathInfo(path)
            },
            refreshLibrary);
    }

    private LibraryStructureController CreateController()
        => new(_serverConfigurationManager, _libraryManager, _libraryMonitor);

    private static bool HasLocation(VirtualFolderInfo folder, string normalizedPath)
        => folder.Locations is not null
           && folder.Locations
               .Where(location => !string.IsNullOrWhiteSpace(location))
               .Any(location => string.Equals(
                   StrmPathBuilder.NormalizePath(location),
                   normalizedPath,
                   StringComparison.OrdinalIgnoreCase));

    private static LibraryOptions CreateLibraryOptions(string path)
        => new()
        {
            Enabled = true,
            EnableRealtimeMonitor = true,
            PathInfos = [new MediaPathInfo(path)]
        };
}
