// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.Script.Config;
using Microsoft.Azure.WebJobs.Script.WebHost;
using Microsoft.Azure.WebJobs.Script.WebHost.Authentication;
using Microsoft.Azure.WebJobs.Script.WebHost.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.WebJobs.Script.Tests;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests
{
    public class StandbyManagerTests : IDisposable
    {
        private readonly ScriptSettingsManager _settingsManager;
        private TestLoggerProvider _loggerProvider;
        private WebHostSettings _webHostSettings;
        private HttpClient _httpClient;
        private TestServer _httpServer;

        public StandbyManagerTests()
        {
            _settingsManager = ScriptSettingsManager.Instance;
            WebScriptHostManager.ResetStandbyMode();
        }

        [Fact]
        public void IsWarmUpRequest_ReturnsExpectedValue()
        {
            var request = HttpTestHelpers.CreateHttpRequest("POST", "http://azure.com/api/warmup");
            Assert.False(StandbyManager.IsWarmUpRequest(request));

            var vars = new Dictionary<string, string>
            {
                { EnvironmentSettingNames.AzureWebsitePlaceholderMode, "0" },
                { EnvironmentSettingNames.AzureWebsiteInstanceId, null }
            };
            using (var env = new TestScopedEnvironmentVariable(vars))
            {
                // in this test we're forcing a transition from non-placeholder mode to placeholder mode
                // which can't happen in the wild, so we force a reset here
                WebScriptHostManager.ResetStandbyMode();

                _settingsManager.SetSetting(EnvironmentSettingNames.AzureWebsitePlaceholderMode, "1");
                Assert.False(StandbyManager.IsWarmUpRequest(request));

                _settingsManager.SetSetting(EnvironmentSettingNames.AzureWebsiteInstanceId, "12345");
                Assert.True(StandbyManager.IsWarmUpRequest(request));

                request = HttpTestHelpers.CreateHttpRequest("POST", "http://azure.com/api/csharphttpwarmup");
                Assert.True(StandbyManager.IsWarmUpRequest(request));

                request = HttpTestHelpers.CreateHttpRequest("POST", "http://azure.com/api/warmup");
                request.Headers.Add(ScriptConstants.AntaresLogIdHeaderName, "xyz123");
                Assert.False(StandbyManager.IsWarmUpRequest(request));

                request = HttpTestHelpers.CreateHttpRequest("POST", "http://azure.com/api/foo");
                Assert.False(StandbyManager.IsWarmUpRequest(request));
            }

            vars = new Dictionary<string, string>
            {
                { EnvironmentSettingNames.AzureWebsitePlaceholderMode, "0" },
                { EnvironmentSettingNames.AzureWebsiteInstanceId, null }
            };
            using (var env = new TestScopedEnvironmentVariable(vars))
            {
                WebScriptHostManager.ResetStandbyMode();

                _settingsManager.SetSetting(EnvironmentSettingNames.AzureWebsitePlaceholderMode, "1");
                Assert.False(StandbyManager.IsWarmUpRequest(request));

                request = HttpTestHelpers.CreateHttpRequest("POST", "http://azure.com/api/warmup");
                _settingsManager.SetSetting(EnvironmentSettingNames.ContainerName, "TestContainer");
                Assert.True(_settingsManager.IsLinuxContainerEnvironment);
                Assert.True(StandbyManager.IsWarmUpRequest(request));
            }
        }

        [Fact]
        public async Task StandbyMode_EndToEnd()
        {
            var vars = new Dictionary<string, string>
            {
                { EnvironmentSettingNames.AzureWebsitePlaceholderMode, "1" },
                { EnvironmentSettingNames.AzureWebsiteContainerReady, null },
                { EnvironmentSettingNames.AzureWebsiteInstanceId, "87654639876900123453445678890144" },
                { "AzureWebEncryptionKey", "0F75CA46E7EBDD39E4CA6B074D1F9A5972B849A55F91A248" }
            };
            using (var env = new TestScopedEnvironmentVariable(vars))
            {
                await InitializeTestHost("StandbyModeTest");

                await VerifyWarmupSucceeds();
                await VerifyWarmupSucceeds(restart: true);

                // now specialize the host
                ScriptSettingsManager.Instance.SetSetting(EnvironmentSettingNames.AzureWebsitePlaceholderMode, "0");
                ScriptSettingsManager.Instance.SetSetting(EnvironmentSettingNames.AzureWebsiteContainerReady, "1");

                Assert.False(WebScriptHostManager.InStandbyMode);
                Assert.True(ScriptSettingsManager.Instance.ContainerReady);

                // give time for the specialization to happen
                string[] logLines = null;
                await TestHelpers.Await(() =>
                {
                    // wait for the trace indicating that the host has been specialized
                    logLines = _loggerProvider.GetAllLogMessages().Where(p => p.FormattedMessage != null).Select(p => p.FormattedMessage).ToArray();
                    return logLines.Contains("Generating 0 job function(s)");
                }, userMessageCallback: () => string.Join(Environment.NewLine, _loggerProvider.GetAllLogMessages().Select(p => $"[{p.Timestamp.ToString("HH:mm:ss.fff")}] {p.FormattedMessage}")));

                _httpServer.Dispose();
                _httpClient.Dispose();

                await Task.Delay(2000);

                var hostConfig = WebHostResolver.CreateScriptHostConfiguration(_webHostSettings, true);
                var expectedHostId = hostConfig.HostConfig.HostId;

                // verify the rest of the expected logs
                string text = string.Join(Environment.NewLine, logLines);
                Assert.True(logLines.Count(p => p.Contains("Stopping Host")) >= 1);
                Assert.Equal(1, logLines.Count(p => p.Contains("Creating StandbyMode placeholder function directory")));
                Assert.Equal(1, logLines.Count(p => p.Contains("StandbyMode placeholder function directory created")));
                Assert.Equal(2, logLines.Count(p => p.Contains("Starting Host (HostId=placeholder-host")));
                Assert.Equal(2, logLines.Count(p => p.Contains("Host is in standby mode")));
                Assert.Equal(2, logLines.Count(p => p.Contains("Executed 'Functions.WarmUp' (Succeeded")));
                Assert.Equal(1, logLines.Count(p => p.Contains("Starting host specialization")));
                Assert.Equal(1, logLines.Count(p => p.Contains($"Starting Host (HostId={expectedHostId}")));
                Assert.Contains("Generating 0 job function(s)", logLines);

                WebScriptHostManager.ResetStandbyMode();
            }
        }

        [Fact]
        public async Task StandbyMode_EndToEnd_LinuxContainer()
        {
            byte[] bytes = TestHelpers.GenerateKeyBytes();
            var encryptionKey = Convert.ToBase64String(bytes);

            var vars = new Dictionary<string, string>
            {
                { EnvironmentSettingNames.ContainerName, "TestContainer" },
                { EnvironmentSettingNames.ContainerEncryptionKey, encryptionKey },
                { EnvironmentSettingNames.AzureWebsiteContainerReady, null },
                { "AzureWebEncryptionKey", "0F75CA46E7EBDD39E4CA6B074D1F9A5972B849A55F91A248" }
            };
            using (var env = new TestScopedEnvironmentVariable(vars))
            {
                await InitializeTestHost("StandbyModeTest_Linux");

                await VerifyWarmupSucceeds();
                await VerifyWarmupSucceeds(restart: true);

                // now specialize the site
                await Assign(encryptionKey);

                Assert.False(WebScriptHostManager.InStandbyMode);
                Assert.True(ScriptSettingsManager.Instance.ContainerReady);

                // immediately call a function - expect the call to block until
                // the host is fully specialized
                var request = new HttpRequestMessage(HttpMethod.Get, "api/httptrigger");
                var response = await _httpClient.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                // verify warmup function no longer there
                request = new HttpRequestMessage(HttpMethod.Get, "api/warmup");
                response = await _httpClient.SendAsync(request);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

                _httpServer.Dispose();
                _httpClient.Dispose();

                await Task.Delay(2000);

                var hostConfig = WebHostResolver.CreateScriptHostConfiguration(_webHostSettings, true);
                var expectedHostId = hostConfig.HostConfig.HostId;

                // verify the expected logs
                var logLines = _loggerProvider.GetAllLogMessages().Where(p => p.FormattedMessage != null).Select(p => p.FormattedMessage).ToArray();
                string text = string.Join(Environment.NewLine, logLines);
                Assert.True(logLines.Count(p => p.Contains("Stopping Host")) >= 1);
                Assert.Equal(1, logLines.Count(p => p.Contains("Creating StandbyMode placeholder function directory")));
                Assert.Equal(1, logLines.Count(p => p.Contains("StandbyMode placeholder function directory created")));
                Assert.Equal(2, logLines.Count(p => p.Contains("Starting Host (HostId=placeholder-host")));
                Assert.Equal(2, logLines.Count(p => p.Contains("Host is in standby mode")));
                Assert.Equal(2, logLines.Count(p => p.Contains("Executed 'Functions.WarmUp' (Succeeded")));
                Assert.Equal(1, logLines.Count(p => p.Contains("Starting host specialization")));
                Assert.Equal(1, logLines.Count(p => p.Contains($"Starting Host (HostId={expectedHostId}")));
                Assert.Contains("Node.js HttpTrigger function invoked.", logLines);

                WebScriptHostManager.ResetStandbyMode();
            }
        }

        private async Task InitializeTestHost(string testDirName)
        {
            var httpConfig = new HttpConfiguration();
            var testRootPath = Path.Combine(Path.GetTempPath(), testDirName);
            await FileUtility.DeleteDirectoryAsync(testRootPath, true);

            _loggerProvider = new TestLoggerProvider();
            var loggerProviderFactory = new TestLoggerProviderFactory(_loggerProvider);
            _webHostSettings = new WebHostSettings
            {
                IsSelfHost = true,
                LogPath = Path.Combine(testRootPath, "Logs"),
                SecretsPath = Path.Combine(testRootPath, "Secrets"),
                ScriptPath = Path.Combine(testRootPath, "WWWRoot")
            };

            var loggerFactory = new LoggerFactory();
            loggerFactory.AddProvider(_loggerProvider);

            var webHostBuilder = Program.CreateWebHostBuilder()
                .ConfigureServices(c =>
                {
                    c.AddSingleton(_webHostSettings)
                    .AddSingleton<ILoggerProviderFactory>(loggerProviderFactory)
                    .AddSingleton<ILoggerFactory>(loggerFactory);
                });

            _httpServer = new TestServer(webHostBuilder);
            _httpClient = _httpServer.CreateClient();
            _httpClient.BaseAddress = new Uri("https://localhost/");

            TestHelpers.WaitForWebHost(_httpClient);

            var traces = _loggerProvider.GetAllLogMessages().ToArray();
            Assert.NotNull(traces.Single(p => p.FormattedMessage.StartsWith("Starting Host (HostId=placeholder-host")));
            Assert.NotNull(traces.Single(p => p.FormattedMessage.StartsWith("Host is in standby mode")));
        }

        private async Task Assign(string encryptionKey)
        {
            // create a zip package
            var contentRoot = Path.Combine(Path.GetTempPath(), @"FunctionsTest");
            var sourcePath = Path.Combine(Directory.GetCurrentDirectory(), @"TestScripts\Node\HttpTrigger");
            var zipFilePath = Path.Combine(contentRoot, "content.zip");
            await CreateContentZip(contentRoot, zipFilePath, @"TestScripts\Node\HttpTrigger");

            // upload the blob and get a SAS uri
            var sasUri = await CreateBlobSas(zipFilePath, "azure-functions-test", "appcontents.zip");

            // Now specialize the host by invoking assign
            var secretManager = _httpServer.Host.Services.GetService<ISecretManager>();
            var masterKey = (await secretManager.GetHostSecretsAsync()).MasterKey;
            string uri = "admin/instance/assign";
            var request = new HttpRequestMessage(HttpMethod.Post, uri);
            var environment = new Dictionary<string, string>()
                {
                    { EnvironmentSettingNames.AzureWebsiteZipDeployment, sasUri.ToString() }
                };
            var assignmentContext = new HostAssignmentContext
            {
                SiteId = 1234,
                SiteName = "TestSite",
                Environment = environment
            };
            var encryptedAssignmentContext = EncryptedHostAssignmentContext.Create(assignmentContext, encryptionKey);
            string json = JsonConvert.SerializeObject(encryptedAssignmentContext);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            request.Headers.Add(AuthenticationLevelHandler.FunctionsKeyHeaderName, masterKey);
            var response = await _httpClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        private async Task VerifyWarmupSucceeds(bool restart = false)
        {
            string uri = "api/warmup";
            if (restart)
            {
                uri += "?restart=1";
            }

            // issue warmup request and verify
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await _httpClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            string responseBody = await response.Content.ReadAsStringAsync();
            Assert.Equal("WarmUp complete.", responseBody);
        }

        private static async Task CreateContentZip(string contentRoot, string zipPath, params string[] copyDirs)
        {
            var contentTemp = Path.Combine(contentRoot, @"ZipContent");
            await FileUtility.DeleteDirectoryAsync(contentTemp, true);

            foreach (var sourceDir in copyDirs)
            {
                var directoryName = Path.GetFileName(sourceDir);
                var targetPath = Path.Combine(contentTemp, directoryName);
                FileUtility.EnsureDirectoryExists(targetPath);
                var sourcePath = Path.Combine(Directory.GetCurrentDirectory(), sourceDir);
                FileUtility.CopyDirectory(sourcePath, targetPath);
            }

            var hostConfigPath = Path.Combine(contentTemp, "host.json");
            File.WriteAllText(hostConfigPath, "{}");

            FileUtility.DeleteFileSafe(zipPath);
            ZipFile.CreateFromDirectory(contentTemp, zipPath);
        }

        private static async Task<Uri> CreateBlobSas(string filePath, string blobContainer, string blobName)
        {
            string connectionString = AmbientConnectionStringProvider.Instance.GetConnectionString(ConnectionStringNames.Storage);
            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(connectionString);
            var blobClient = storageAccount.CreateCloudBlobClient();
            var container = blobClient.GetContainerReference(blobContainer);
            await container.CreateIfNotExistsAsync();
            var blob = container.GetBlockBlobReference(blobName);
            await blob.UploadFromFileAsync(filePath);
            var policy = new SharedAccessBlobPolicy
            {
                SharedAccessStartTime = DateTime.UtcNow,
                SharedAccessExpiryTime = DateTime.UtcNow.AddHours(1),
                Permissions = SharedAccessBlobPermissions.Read | SharedAccessBlobPermissions.List
            };
            var sas = blob.GetSharedAccessSignature(policy);
            var sasUri = new Uri(blob.Uri, sas);

            return sasUri;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(EnvironmentSettingNames.ContainerName, string.Empty);
        }
    }
}
