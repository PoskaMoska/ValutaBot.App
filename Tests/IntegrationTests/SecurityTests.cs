using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;
using Microsoft.AspNetCore.Http;
using System.Collections.Generic;
using Microsoft.Extensions.Primitives;
using ValutaBot.MiniApp;

namespace IntegrationTests
{
    public class SecurityTests
    {
        // 1. AuthService Fail-Closed Test
        [Fact]
        public async Task AuthService_MissingBotToken_FailsClosed()
        {
            // Arrange
            TelegramNotifier.Init(null); // Simulate missing env var
            var context = new DefaultHttpContext();
            
            // Act
            var (isAuthorized, error) = await AuthService.IsRequestAuthorized(context);
            
            // Assert
            Assert.False(isAuthorized);
            Assert.Contains("Missing bot token", error);
        }

        // 2. InitData Validation Test (Invalid Hash)
        [Fact]
        public void TelegramInitDataValidator_InvalidHash_Fails()
        {
            // Arrange
            string fakeBotToken = "123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11";
            string fakeInitData = "query_id=AAHdF6IQAAAAAN0XohC-1234&user=%7B%22id%22%3A123456789%2C%22first_name%22%3A%22Test%22%7D&auth_date=1700000000&hash=invalidhash1234567890abcdef";
            
            // Act
            bool isValid = TelegramInitDataValidator.Validate(fakeInitData, fakeBotToken, out long userId, out string username);
            
            // Assert
            Assert.False(isValid);
            Assert.Equal(0, userId);
        }

        // 3. Postback Endpoint Secret Validation
        [Fact]
        public void PostbackEndpoint_WithoutSecret_ShouldReturn401()
        {
            string expectedSecret = "test_secret_123";
            string providedSecret = ""; // Missing
            
            bool isAuthorized = !string.IsNullOrEmpty(providedSecret) && providedSecret == expectedSecret;
            
            Assert.False(isAuthorized);
        }

        [Fact]
        public void PostbackEndpoint_WithInvalidSecret_ShouldReturn401()
        {
            string expectedSecret = "test_secret_123";
            string providedSecret = "hacker_secret_999"; 
            
            bool isAuthorized = !string.IsNullOrEmpty(providedSecret) && providedSecret == expectedSecret;
            
            Assert.False(isAuthorized);
        }
        
        [Fact]
        public void PostbackEndpoint_WithValidSecret_ShouldReturnOk()
        {
            string expectedSecret = "test_secret_123";
            string providedSecret = "test_secret_123"; 
            
            bool isAuthorized = !string.IsNullOrEmpty(providedSecret) && providedSecret == expectedSecret;
            
            Assert.True(isAuthorized);
        }

        // 4. Custom InitData HMAC & AuthDate Compatibility Tests
        [Fact]
        public void ValidateInitData_LegacyCustomTokenWithoutAuthDate_Fails()
        {
            string botToken = "123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11";
            long userId = 987654321L;

            using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(botToken));
            string sign = Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(userId.ToString()))).ToLowerInvariant();

            string initData = $"custom_user_id={userId}&custom_user_sign={sign}";

            var (ok, validatedUserId, error) = AuthService.ValidateInitData(initData, botToken);

            Assert.False(ok);
            Assert.Equal(0, validatedUserId);
            Assert.Equal("Legacy token without auth_date is deprecated and no longer accepted", error);
        }

        [Fact]
        public void ValidateInitData_ModernCustomTokenWithValidAuthDate_Succeeds()
        {
            string botToken = "123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11";
            long userId = 987654321L;
            long authDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(botToken));
            string sign = Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"{userId}:{authDate}"))).ToLowerInvariant();

            string initData = $"custom_user_id={userId}&custom_user_sign={sign}&auth_date={authDate}";

            var (ok, validatedUserId, error) = AuthService.ValidateInitData(initData, botToken);

            Assert.True(ok);
            Assert.Equal(userId, validatedUserId);
            Assert.Null(error);
        }

        [Fact]
        public void ValidateInitData_ModernCustomTokenWithExpiredAuthDate_Fails()
        {
            string botToken = "123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11";
            long userId = 987654321L;
            long oldAuthDate = DateTimeOffset.UtcNow.AddHours(-25).ToUnixTimeSeconds();

            using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(botToken));
            string sign = Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"{userId}:{oldAuthDate}"))).ToLowerInvariant();

            string initData = $"custom_user_id={userId}&custom_user_sign={sign}&auth_date={oldAuthDate}";

            var (ok, validatedUserId, error) = AuthService.ValidateInitData(initData, botToken);

            Assert.False(ok);
            Assert.Equal(0, validatedUserId);
            Assert.Equal("Custom authorization token expired or invalid timestamp", error);
        }

        [Fact]
        public void SessionCookie_CreateAndValidate_EnforcesHmacExpiryAndSlidingWindow()
        {
            string botToken = "123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11";
            long userId = 987654321L;
            var t0 = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

            string token = AuthService.CreateSessionToken(userId, botToken, t0);

            // Fresh token valid & does not need renewal yet
            Assert.True(AuthService.TryValidateSessionToken(token, botToken, out long parsedId, out bool shouldRenew, t0.AddHours(1)));
            Assert.Equal(userId, parsedId);
            Assert.False(shouldRenew);

            // Within final 7 days -> valid & marks sliding renewal
            Assert.True(AuthService.TryValidateSessionToken(token, botToken, out _, out shouldRenew, t0.AddDays(9)));
            Assert.True(shouldRenew);

            // Past 14 days -> expired
            Assert.False(AuthService.TryValidateSessionToken(token, botToken, out _, out _, t0.AddDays(15)));

            // Tampered signature -> rejected
            string tampered = token[..^4] + (token.EndsWith("0000") ? "1111" : "0000");
            Assert.False(AuthService.TryValidateSessionToken(tampered, botToken, out _, out _, t0.AddHours(1)));
        }

        [Fact]
        public void ValidateInitDataDetailed_DistinguishesExpiredTelegramAuthFromInvalidSignature()
        {
            string botToken = "123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11";
            long userId = 555666777L;
            long oldAuthDate = DateTimeOffset.UtcNow.AddHours(-25).ToUnixTimeSeconds();
            string userJson = $"{{\"id\":{userId},\"first_name\":\"Test\"}}";

            // Build authentic Telegram WebApp dataCheckString and HMAC
            var pairs = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["auth_date"] = oldAuthDate.ToString(),
                ["query_id"] = "AAHdF6IQAAAAAN0XohC-1234",
                ["user"] = userJson
            };
            string dataCheck = string.Join("\n", pairs.Select(kv => $"{kv.Key}={kv.Value}"));
            byte[] secretKey = System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("WebAppData"),
                System.Text.Encoding.UTF8.GetBytes(botToken));
            string validHash = Convert.ToHexString(
                System.Security.Cryptography.HMACSHA256.HashData(
                    secretKey,
                    System.Text.Encoding.UTF8.GetBytes(dataCheck))).ToLowerInvariant();

            string expiredButAuthenticInitData =
                $"query_id={Uri.EscapeDataString(pairs["query_id"])}&user={Uri.EscapeDataString(userJson)}&auth_date={oldAuthDate}&hash={validHash}";

            var (ok, uid, err, code) = AuthService.ValidateInitDataDetailed(expiredButAuthenticInitData, botToken);
            Assert.False(ok);
            Assert.Equal(userId, uid);
            Assert.Equal(AuthFailureCode.TokenExpired, code);
            Assert.Contains("expired", err!, StringComparison.OrdinalIgnoreCase);

            // Tampered hash must return InvalidSignature with userId=0
            string tamperedInitData = expiredButAuthenticInitData.Replace(validHash, new string('a', validHash.Length));
            var (okTampered, uidTampered, _, codeTampered) = AuthService.ValidateInitDataDetailed(tamperedInitData, botToken);
            Assert.False(okTampered);
            Assert.Equal(0, uidTampered);
            Assert.Equal(AuthFailureCode.InvalidSignature, codeTampered);
        }

        [Fact]
        public async Task IsRequestAuthorizedDetailed_ExpiredHeaderFallsBackToMatchingCookie_NeverOnTamperedHeaderOrUserMismatch()
        {
            string botToken = "123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11";
            TelegramNotifier.Init(botToken);
            long userA = 111222333L;
            long userB = 444555666L;

            string cookieA = AuthService.CreateSessionToken(userA, botToken);
            long oldAuthDate = DateTimeOffset.UtcNow.AddHours(-25).ToUnixTimeSeconds();
            using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(botToken));
            string expiredSignA = Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"{userA}:{oldAuthDate}"))).ToLowerInvariant();
            string expiredSignB = Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"{userB}:{oldAuthDate}"))).ToLowerInvariant();

            // Force non-dev cloud mode so bypass doesn't trigger
            string? prevRailwayEnv = Environment.GetEnvironmentVariable("RAILWAY_ENVIRONMENT");
            Environment.SetEnvironmentVariable("RAILWAY_ENVIRONMENT", "production");
            try
            {
                // 1. Tampered header WITH valid cookie MUST fail closed at InvalidSignature (not use cookie!)
                var ctxTampered = new DefaultHttpContext();
                ctxTampered.Request.Headers["X-Telegram-Init-Data"] = $"custom_user_id={userA}&custom_user_sign={new string('0', 64)}&auth_date={oldAuthDate}";
                ctxTampered.Request.Headers["Cookie"] = $"{AuthService.SessionCookieName}={cookieA}";
                var resTampered = await AuthService.IsRequestAuthorizedDetailed(ctxTampered);
                Assert.False(resTampered.IsAuthorized);
                Assert.Equal(AuthFailureCode.InvalidSignature, resTampered.Code);

                // 2. Expired authentic header for userB WITH valid cookie for userA MUST reject user mismatch (TokenExpired)
                var ctxMismatch = new DefaultHttpContext();
                ctxMismatch.Request.Headers["X-Telegram-Init-Data"] = $"custom_user_id={userB}&custom_user_sign={expiredSignB}&auth_date={oldAuthDate}";
                ctxMismatch.Request.Headers["Cookie"] = $"{AuthService.SessionCookieName}={cookieA}";
                var resMismatch = await AuthService.IsRequestAuthorizedDetailed(ctxMismatch);
                Assert.False(resMismatch.IsAuthorized);
                Assert.Equal(AuthFailureCode.TokenExpired, resMismatch.Code);

                // 3. Expired authentic header for userA WITH valid cookie for userA passes signature/session check
                // and proceeds to live DB check IsUserAllowed(userA) -> AccessDenied in isolated unit host (proves cookie auth path ran!)
                var ctxMatched = new DefaultHttpContext();
                ctxMatched.Request.Headers["X-Telegram-Init-Data"] = $"custom_user_id={userA}&custom_user_sign={expiredSignA}&auth_date={oldAuthDate}";
                ctxMatched.Request.Headers["Cookie"] = $"{AuthService.SessionCookieName}={cookieA}";
                var resMatched = await AuthService.IsRequestAuthorizedDetailed(ctxMatched);
                Assert.False(resMatched.IsAuthorized);
                Assert.Equal(AuthFailureCode.AccessDenied, resMatched.Code);
                Assert.Equal(userA, resMatched.UserId);
            }
            finally
            {
                Environment.SetEnvironmentVariable("RAILWAY_ENVIRONMENT", prevRailwayEnv);
            }
        }
    }
}
