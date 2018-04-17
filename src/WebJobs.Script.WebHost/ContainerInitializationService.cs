// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Config;
using Microsoft.Azure.WebJobs.Script.WebHost.Management;
using Microsoft.Azure.WebJobs.Script.WebHost.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage.Blob;

namespace Microsoft.Azure.WebJobs.Script.WebHost
{
    public class ContainerInitializationService : IHostedService, IDisposable
    {
        private readonly ScriptSettingsManager _settingsManager;
        private readonly IInstanceManager _instanceManager;
        private readonly ILogger _logger;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _executingTask;
        private bool _disposed = false;

        public ContainerInitializationService(ScriptSettingsManager settingsManager, IInstanceManager instanceManager, ILoggerFactory loggerFactory)
        {
            _settingsManager = settingsManager;
            _instanceManager = instanceManager;
            _executingTask = Task.CompletedTask;
            _logger = loggerFactory.CreateLogger(nameof(ContainerInitializationService));
        }

        private async Task<string> GetAssignmentContextFromSasUri(string sasUri)
        {
            var cloudBlockBlob = new CloudBlockBlob(new Uri(sasUri));

            try
            {
                var exists = await cloudBlockBlob.ExistsAsync(null, null, _cancellationTokenSource.Token);
                if (exists)
                {
                    var startContext = await cloudBlockBlob.DownloadTextAsync(null, null, null, null, _cancellationTokenSource.Token);
                    return startContext;
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error calling {nameof(GetAssignmentContextFromSasUri)}");
            }

            return string.Empty;
        }

        private async Task InitializeAssignmentContext()
        {
            var startContext = _settingsManager.GetSetting(EnvironmentSettingNames.ContainerStartContext);

            // Container start context is not available directly
            if (string.IsNullOrEmpty(startContext))
            {
                _logger.LogInformation("AssignmnetContext not available in ContainerStartContext");
                // Check if the context is available in blob
                var sasUri = _settingsManager.GetSetting(EnvironmentSettingNames.ContainerStartContextSasUri);

                if (!string.IsNullOrEmpty(sasUri))
                {
                    _logger.LogInformation("AssignmnetContext available in ContainerStartContextSasUri");
                    startContext = await GetAssignmentContextFromSasUri(sasUri);
                }
            }

            if (!string.IsNullOrEmpty(startContext))
            {
                _logger.LogInformation("Initializing container..");
                var encryptedAssignmentContext = (EncryptedAssignmentContext)Convert.ChangeType(startContext, typeof(EncryptedAssignmentContext));
                var containerKey = _settingsManager.GetSetting(ScriptConstants.ContainerEncryptionKey);
                var assignmentContext = encryptedAssignmentContext.Decrypt(containerKey);
                _instanceManager.TryAssign(assignmentContext);
            }
            else
            {
                _logger.LogInformation("Waiting for InstanceController.Assign to receive AssignmentContext");
            }
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Initializing ContainerInitializationService.");
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _executingTask = InitializeAssignmentContext();
            return _executingTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _cancellationTokenSource.Cancel();

            var task = await Task.WhenAny(_executingTask, Task.Delay(TimeSpan.FromSeconds(10)));
            if (task != _executingTask)
            {
                _logger.LogWarning("ContainerInitializationService shutdown incomplete");
            }
            else
            {
                _logger.LogInformation("ContainerInitializationService shutdown complete");
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _cancellationTokenSource.Dispose();
                }
                _disposed = true;
            }
        }

        public void Dispose() => Dispose(true);
    }
}
