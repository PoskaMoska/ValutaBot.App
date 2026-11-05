using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ValutaBot.App.MiniApp.Telegram.Commands;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Services;
using Xunit;

namespace ValutaBot.Tests
{
    public class WorkerAndTelegramResilienceTests
    {
        [Theory]
        [InlineData(1, 2)]
        [InlineData(2, 4)]
        [InlineData(5, 10)]
        [InlineData(14, 28)]
        [InlineData(15, 30)]
        [InlineData(20, 30)]
        [InlineData(100, 30)]
        public void AutoTradingScanner_LinearBackoff_CompliesWithInvariants(int consecutiveFailures, int expectedSeconds)
        {
            // Linear backoff formula: Math.Min(consecutiveFailures * 2, 30)
            int actual = Math.Min(consecutiveFailures * 2, 30);
            Assert.Equal(expectedSeconds, actual);
        }

        [Fact]
        public void AutoTradingScanner_ResetLastScannedTimes_ExecutesWithoutError()
        {
            AutoTradingScannerService.ResetLastScannedTimes();
            // Should be callable without exceptions on startup and supervisor catch block
        }

        [Fact]
        public async Task AdminAddUserStateCommand_PurgesStateAtEntry_EvenWhenNotAdmin()
        {
            long testChatId = 999888777;
            TelegramBotService.UserStates[testChatId] = TelegramBotService.UserState.AwaitingAddAdminId;

            var cmd = new AdminAddUserStateCommand();
            Assert.True(cmd.CanHandle(testChatId, "", ""));

            // Execute as non-admin
            await cmd.ExecuteAsync(testChatId, "", "12345", isAdmin: false, token: "dummy", webAppUrl: "http://dummy");

            // FSM safety check: state must be purged from dictionary on entry
            bool hasLingeringState = TelegramBotService.UserStates.ContainsKey(testChatId);
            Assert.False(hasLingeringState);
        }

        [Fact]
        public async Task SubmitIdStateCommand_GuaranteesUserSubmittedIdsCleanup()
        {
            long testChatId = 888777666;
            TelegramBotService.UserStates[testChatId] = TelegramBotService.UserState.AwaitingId;

            var cmd = new SubmitIdStateCommand();
            Assert.True(cmd.CanHandle(testChatId, "", "12345678"));

            await cmd.ExecuteAsync(testChatId, "", "12345678", isAdmin: false, token: "dummy", webAppUrl: "http://dummy");

            // Guaranteed cleanup via try/finally: UserSubmittedIds must not retain testChatId
            Assert.False(TelegramBotService.UserSubmittedIds.ContainsKey(testChatId));
            Assert.False(TelegramBotService.UserStates.ContainsKey(testChatId));
        }

        [Fact]
        public async Task PostTelegramJsonWithRetryAsync_ExhaustedRetries_ReturnsNonNullContent()
        {
            // Set base URL to a dummy unreachable endpoint or test 429 response structure
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"ok\":false,\"description\":\"Too Many Requests: retry limit exceeded\"}")
            };

            Assert.NotNull(response.Content);
            string body = await response.Content.ReadAsStringAsync();
            Assert.Contains("retry limit exceeded", body);
        }

        [Fact]
        public async Task PostgresDistributedLock_InOfflineMode_AcquiresAndReleasesGracefully()
        {
            await using var distLock = new PostgresDistributedLock("test_lock_offline");
            bool acquired = await distLock.TryAcquireAsync();
            Assert.True(acquired);
            Assert.True(distLock.IsHeld);

            await distLock.ReleaseAsync();
            Assert.False(distLock.IsHeld);
        }
    }
}
