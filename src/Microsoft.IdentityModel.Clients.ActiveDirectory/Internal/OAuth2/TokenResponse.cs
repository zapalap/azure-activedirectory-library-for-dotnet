﻿//----------------------------------------------------------------------
//
// Copyright (c) Microsoft Corporation.
// All rights reserved.
//
// This code is licensed under the MIT License.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files(the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and / or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions :
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
//------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Microsoft.Identity.Core;
using Microsoft.Identity.Core.Cache;
using Microsoft.Identity.Core.Helpers;
using Microsoft.Identity.Core.Http;
using Microsoft.IdentityModel.Clients.ActiveDirectory.Internal.Helpers;
using Microsoft.IdentityModel.Clients.ActiveDirectory.Internal.Http;
using Microsoft.IdentityModel.Clients.ActiveDirectory.Internal.Instance;
using Microsoft.IdentityModel.Clients.ActiveDirectory.Internal.Platform;

namespace Microsoft.IdentityModel.Clients.ActiveDirectory.Internal.OAuth2
{
    internal static class TokenResponseClaim
    {
        public const string Code = "code";
        public const string TokenType = "token_type";
        public const string AccessToken = "access_token";
        public const string RefreshToken = "refresh_token";
        public const string Resource = "resource";
        public const string IdToken = "id_token";
        public const string CreatedOn = "created_on";
        public const string ExpiresOn = "expires_on";
        public const string ExpiresIn = "expires_in";
        public const string ExtendedExpiresIn = "ext_expires_in";
        public const string Error = "error";
        public const string ErrorDescription = "error_description";
        public const string ErrorCodes = "error_codes";
        public const string Claims = "claims";
        public const string CloudInstanceHost = "cloud_instance_host_name";
        public const string Authority = "authority";
        public const string ClientInfo = "client_info";
    }

    [DataContract]
    internal class TokenResponse
    {
        private const string CorrelationIdClaim = "correlation_id";

        [DataMember(Name = TokenResponseClaim.TokenType, IsRequired = false)]
        public string TokenType { get; set; }

        [DataMember(Name = TokenResponseClaim.AccessToken, IsRequired = false)]
        public string AccessToken { get; set; }

        [DataMember(Name = TokenResponseClaim.RefreshToken, IsRequired = false)]
        public string RefreshToken { get; set; }

        [DataMember(Name = TokenResponseClaim.Resource, IsRequired = false)]
        public string Resource { get; set; }

        [DataMember(Name = TokenResponseClaim.IdToken, IsRequired = false)]
        public string IdTokenString { get; set; }

        [DataMember(Name = TokenResponseClaim.CreatedOn, IsRequired = false)]
        public long CreatedOn { get; set; }

        [DataMember(Name = TokenResponseClaim.ExpiresOn, IsRequired = false)]
        public long ExpiresOn { get; set; }

        [DataMember(Name = TokenResponseClaim.ExpiresIn, IsRequired = false)]
        public long ExpiresIn { get; set; }

        [DataMember(Name = TokenResponseClaim.ExtendedExpiresIn, IsRequired = false)]
        public long ExtendedExpiresIn { get; set; }

        [DataMember(Name = TokenResponseClaim.Error, IsRequired = false)]
        public string Error { get; set; }

        [DataMember(Name = TokenResponseClaim.ErrorDescription, IsRequired = false)]
        public string ErrorDescription { get; set; }

        [DataMember(Name = TokenResponseClaim.ErrorCodes, IsRequired = false)]
        public string[] ErrorCodes { get; set; }

        [DataMember(Name = CorrelationIdClaim, IsRequired = false)]
        public string CorrelationId { get; set; }

        [DataMember(Name = TokenResponseClaim.Claims, IsRequired = false)]
        public string Claims { get; set; }

        [DataMember(Name = TokenResponseClaim.ClientInfo, IsRequired = false)]
        public string ClientInfo { get; set; }

        public string Authority { get; set; }

        internal static TokenResponse CreateFromBrokerResponse(IDictionary<string, string> responseDictionary)
        {
            if (responseDictionary.ContainsKey(TokenResponseClaim.ErrorDescription))
            {
                return new TokenResponse
                {
                    Error = responseDictionary[TokenResponseClaim.Error],
                    ErrorDescription = responseDictionary[TokenResponseClaim.ErrorDescription]
                };
            }

            return new TokenResponse
            {
                Authority = responseDictionary.ContainsKey("authority")
                    ? Authenticator.EnsureUrlEndsWithForwardSlash(EncodingHelper.UrlDecode(responseDictionary["authority"]))
                    : null,
                AccessToken = responseDictionary["access_token"],
                RefreshToken = responseDictionary.ContainsKey("refresh_token")
                    ? responseDictionary["refresh_token"]
                    : null,
                IdTokenString = responseDictionary["id_token"],
                TokenType = "Bearer",
                CorrelationId = responseDictionary["correlation_id"],
                Resource = responseDictionary["resource"],
                ExpiresOn = long.Parse(responseDictionary["expires_on"].Split('.')[0], CultureInfo.CurrentCulture),
                ClientInfo = responseDictionary.ContainsKey("client_info")
                ? responseDictionary["client_info"]
                : null,
            };
        }

        public static TokenResponse CreateFromErrorResponse(IHttpWebResponse webResponse)
        {
            if (webResponse == null)
            {
                return new TokenResponse
                {
                    Error = AdalError.ServiceReturnedError,
                    ErrorDescription = AdalErrorMessage.ServiceReturnedError
                };
            }

            TokenResponse tokenResponse = null;
            using (Stream responseStream = EncodingHelper.GenerateStreamFromString(webResponse.Body))
            {
                string responseStreamString = ReadStreamContent(responseStream);

                try
                {
                    tokenResponse = JsonHelper.DeserializeFromJson<TokenResponse>(responseStreamString);
                }
                catch (SerializationException)
                {
                    tokenResponse = new TokenResponse
                    {
                        Error = (webResponse.StatusCode == HttpStatusCode.ServiceUnavailable)
                            ? AdalError.ServiceUnavailable
                            : AdalError.Unknown,
                        ErrorDescription = responseStreamString
                    };
                }
            }

            return tokenResponse;
        }

        public AdalResultWrapper GetResult()
        {
            // extendedExpiresOn can be less than expiresOn if
            // the server did not return extendedExpiresOn in the
            // token response. Default json deserialization will set
            // the value to 0.
            if (ExtendedExpiresIn < ExpiresIn)
            {
                CoreLoggerBase.Default.Info(string.Format(CultureInfo.InvariantCulture,
                        "ExtendedExpiresIn({0}) is less than ExpiresIn({1}). Set ExpiresIn as ExtendedExpiresIn",
                        this.ExtendedExpiresIn, this.ExpiresIn));
                ExtendedExpiresIn = ExpiresIn;
            }

            return this.GetResult(DateTime.UtcNow + TimeSpan.FromSeconds(this.ExpiresIn),
                DateTime.UtcNow + TimeSpan.FromSeconds(this.ExtendedExpiresIn));
        }

        public AdalResultWrapper GetResult(DateTimeOffset expiresOn, DateTimeOffset extendedExpiresOn)
        {
            AdalResultWrapper resultEx;

            if (this.AccessToken != null)
            {
                var result = new AdalResult(this.TokenType, this.AccessToken, expiresOn, extendedExpiresOn);

                IdToken.TryParse(this.IdTokenString, out IdToken idToken);
                if (idToken != null)
                {
                    string tenantId = idToken.TenantId;
                    string uniqueId = idToken.GetUniqueId();
                    string displayableId = idToken.GetDisplayableId();
                    string givenName = idToken.GivenName;
                    string familyName = idToken.FamilyName;
                    string identityProvider = idToken.IdentityProvider ?? idToken.Issuer;
                    DateTimeOffset? passwordExpiresOffest = null;
                    if (idToken.PasswordExpiration > 0)
                    {
                        passwordExpiresOffest = DateTime.UtcNow + TimeSpan.FromSeconds(idToken.PasswordExpiration);
                    }

                    Uri changePasswordUri = null;
                    if (!string.IsNullOrEmpty(idToken.PasswordChangeUrl))
                    {
                        changePasswordUri = new Uri(idToken.PasswordChangeUrl);
                    }

                    result.UpdateTenantAndUserInfo(tenantId, this.IdTokenString,
                        new AdalUserInfo
                        {
                            UniqueId = uniqueId,
                            DisplayableId = displayableId,
                            GivenName = givenName,
                            FamilyName = familyName,
                            IdentityProvider = identityProvider,
                            PasswordExpiresOn = passwordExpiresOffest,
                            PasswordChangeUrl = changePasswordUri
                        });

                    result.Authority = Authority;
                }

                resultEx = new AdalResultWrapper
                {
                    Result = result,
                    RefreshToken = this.RefreshToken,
                    // This is only needed for AcquireTokenByAuthorizationCode in which parameter resource is optional and we need
                    // to get it from the STS response.
                    ResourceInResponse = this.Resource,
                    RawClientInfo = ClientInfo
                };
            }
            else if (this.Error != null)
            {
                throw new AdalServiceException(this.Error, this.ErrorDescription);
            }
            else
            {
                throw new AdalServiceException(AdalError.Unknown, AdalErrorMessage.Unknown);
            }

            return resultEx;
        }


        private static string ReadStreamContent(Stream stream)
        {
            using (StreamReader sr = new StreamReader(stream))
            {
                return sr.ReadToEnd();
            }
        }
    }
}
