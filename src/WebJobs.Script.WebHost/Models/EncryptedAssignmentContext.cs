// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Azure.WebJobs.Script.WebHost.Helpers;
using Newtonsoft.Json;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Models
{
    public class EncryptedAssignmentContext
    {
        [JsonProperty("encryptedContext")]
        public string EncryptedContext { get; set; }

        public AssignmentContext Decrypt(string key)
        {
            var encryptionKey = Convert.FromBase64String(key);
            var decrypted = SimpleWebTokenHelper.Decrypt(encryptionKey, EncryptedContext);
            return JsonConvert.DeserializeObject<AssignmentContext>(decrypted);
        }
    }
}
