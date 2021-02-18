﻿using System;
using System.IO;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Files;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using OpenMod.API;
using OpenMod.API.Permissions;
using OpenMod.API.Plugins;
using OpenMod.WebServer.Extensions;
using OpenMod.Core.Helpers;
using OpenMod.Core.Plugins;
using OpenMod.EntityFrameworkCore.Extensions;
using OpenMod.Extensions.Games.Abstractions;
using OpenMod.WebServer.Controllers;
using OpenMod.WebServer.Events;
using OpenMod.WebServer.Logging;
using OpenMod.WebServer.Routing;
using SwanLogger = Swan.Logging.Logger;

[assembly: PluginMetadata("OpenMod.WebServer", Author = "OpenMod", DisplayName = "OpenMod WebServer", Website = "https://github.com/openmod/OpenMod.WebServer/")]
namespace OpenMod.WebServer
{
    [UsedImplicitly]
    public class ApiServerPlugin : OpenModUniversalPlugin
    {
        private readonly IRuntime _runtime;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IPermissionRegistry _permissionRegistry;
        private readonly WebServerDbContext _dbContext;
        private EmbedIO.WebServer? _server;

        public ApiServerPlugin(IServiceProvider serviceProvider,
            IRuntime runtime,
            ILoggerFactory loggerFactory,
            IPermissionRegistry permissionRegistry,
            WebServerDbContext dbContext) : base(serviceProvider)
        {
            _runtime = runtime;
            _loggerFactory = loggerFactory;
            _permissionRegistry = permissionRegistry;
            _dbContext = dbContext;
        }

        protected override async Task OnLoadAsync()
        {
            await base.OnLoadAsync();
            RegisterPermissions();

            await _dbContext.OpenModMigrateAsync();

            SwanLogger.NoLogging();
            SwanLogger.RegisterLogger(new SerilogLogger(_loggerFactory.CreateLogger("OpenMod.ApiServer.WebServer")));

            var staticFilesDirectory = Path.Combine(WorkingDirectory, "www");
            if (!Directory.Exists(staticFilesDirectory))
            {
                Directory.CreateDirectory(staticFilesDirectory);
            }

            var url = $"http://{Configuration["bind"]}:{Configuration["port"]}";
            _server = new EmbedIO.WebServer(o => o
                    .WithUrlPrefix(url)
                    .WithMode(HttpListenerMode.EmbedIO));

            var @apiServerConfigurationEvent = new ApiServerConfigurationEvent(_server);
            await EventBus.EmitAsync(this, this, @apiServerConfigurationEvent);

            _server
                .WithOpenModWebApi("/api", module =>
                {
                    if (_runtime.HostInformation is IGameHostInformation)
                    {
                        module.RegisterController<PlayersController>(this);
                    }

                    module.RegisterController<PluginsController>(this);
                    module.RegisterController<SessionController>(this);

                    var @event = new ApiServerConfigureWebApiModuleEvent(_server, module);
                    AsyncHelper.RunSync(() => EventBus.EmitAsync(this, this, @event));
                })
                .WithStaticFolder("/", staticFilesDirectory, true, m =>
                {
                    m.WithContentCaching();
                });

            _server.StateChanged += (s, e) => SwanLogger.Info($"WebServer state - {e.NewState}");
            AsyncHelper.Schedule("API Server", () => _server.RunAsync());
        }

        public void RegisterPermissions()
        {
            _permissionRegistry.RegisterPermission(this, PlayersController.PermissionGetPlayer, "Allows to get information about a player");
            _permissionRegistry.RegisterPermission(this, PlayersController.PermissionGetAllPlayers, "Allows to get all connected players");
            _permissionRegistry.RegisterPermission(this, PluginsController.PermissionGetAllPlugins, "Allows to get all installed plugins");
        }

        protected override Task OnUnloadAsync()
        {
            _server?.Dispose();
            return base.OnUnloadAsync();
        }
    }
}