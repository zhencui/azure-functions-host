// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using Microsoft.ApplicationInsights.SnapshotCollector;
using Microsoft.Azure.WebJobs.Logging.ApplicationInsights;
using Microsoft.Azure.WebJobs.Script.Config;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Newtonsoft.Json;

namespace Microsoft.Azure.WebJobs.Script
{
    /// <summary>
    /// Provides ways to plug into the ScriptHost ILoggerFactory initialization.
    /// </summary>
    public class DefaultLoggerProviderFactory : ILoggerProviderFactory
    {
        public virtual IEnumerable<ILoggerProvider> CreateLoggerProviders(string hostInstanceId, ScriptHostConfiguration scriptConfig, ScriptSettingsManager settingsManager,
            Func<bool> isFileLoggingEnabled, Func<bool> isPrimary)
        {
            IList<ILoggerProvider> providers = new List<ILoggerProvider>();

            IMetricsLogger metricsLogger = scriptConfig.HostConfig.GetService<IMetricsLogger>();

            // Automatically register App Insights if the key is present
            if (!string.IsNullOrEmpty(settingsManager?.ApplicationInsightsInstrumentationKey))
            {
                metricsLogger?.LogEvent(MetricEventNames.ApplicationInsightsEnabled);


                SnapshotCollectorConfiguration snapshotCollectorConfiguration = null;
                if (!string.IsNullOrEmpty(settingsManager?.SnapshotCollectorConfiguration))
                {
                    try
                    {
                        IConfigurationSection configSection = settingsManager?.Configuration?.GetSection("SnapshotCollectorConfiguration");
                        snapshotCollectorConfiguration = configSection.Get<SnapshotCollectorConfiguration>();
                    }
                    catch
                    {
                        // ignore
                    }
                }

                ITelemetryClientFactory clientFactory = scriptConfig.HostConfig.GetService<ITelemetryClientFactory>() ??
                    new ScriptTelemetryClientFactory(settingsManager.ApplicationInsightsInstrumentationKey, scriptConfig.ApplicationInsightsSamplingSettings,
                    snapshotCollectorConfiguration, scriptConfig.LogFilter.Filter);

                providers.Add(new ApplicationInsightsLoggerProvider(clientFactory));
            }
            else
            {
                metricsLogger?.LogEvent(MetricEventNames.ApplicationInsightsDisabled);
            }

            providers.Add(new FunctionFileLoggerProvider(hostInstanceId, scriptConfig.RootLogPath, isFileLoggingEnabled, isPrimary));
            providers.Add(new HostFileLoggerProvider(hostInstanceId, scriptConfig.RootLogPath, isFileLoggingEnabled));

            if (settingsManager.Configuration.GetSection("host:logger:consoleLoggingMode").Value == "always")
            {
                providers.Add(new ConsoleLoggerProvider(scriptConfig.LogFilter.Filter, includeScopes: true));
            }

            return providers;
        }
    }
}