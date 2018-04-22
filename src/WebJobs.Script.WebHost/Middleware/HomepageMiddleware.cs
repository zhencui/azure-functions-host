// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs.Script.Config;
using Microsoft.Azure.WebJobs.Script.WebHost.Features;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Middleware
{
    public class HomepageMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger _logger;

        public HomepageMiddleware(RequestDelegate next, ILoggerFactory loggerFactory)
        {
            _next = next;
            _logger = loggerFactory.CreateLogger(nameof(HomepageMiddleware));
        }

        public static bool IsHomepageDisabled
        {
            get
            {
                return string.Equals(ScriptSettingsManager.Instance.GetSetting(EnvironmentSettingNames.AzureWebJobsDisableHomepage),
                    bool.TrueString, StringComparison.OrdinalIgnoreCase);
            }
        }

        public async Task Invoke(HttpContext context, WebScriptHostManager manager)
        {
            await _next(context);

            IFunctionExecutionFeature functionExecution = context.Features.Get<IFunctionExecutionFeature>();

            if (functionExecution == null
                && context.Request.Path.Value == "/")
            {
                _logger.LogInformation($"GET / called at {DateTime.UtcNow}");

                IActionResult result = null;

                if (IsHomepageDisabled)
                {
                    result = new NoContentResult();
                }
                else
                {
                    result = new ContentResult()
                    {
                        Content = GetHomepage(),
                        ContentType = "text/html",
                        StatusCode = 200
                    };
                }

                if (!context.Response.HasStarted)
                {
                    var actionContext = new ActionContext
                    {
                        HttpContext = context
                    };

                    await result.ExecuteResultAsync(actionContext);
                }
            }
        }

        private string GetHomepage()
        {
            var assembly = typeof(HomepageMiddleware).Assembly;
            using (Stream resource = assembly.GetManifestResourceStream(assembly.GetName().Name + ".Home.html"))
            using (var reader = new StreamReader(resource))
            {
                return reader.ReadToEnd();
            }
        }
    }
}
