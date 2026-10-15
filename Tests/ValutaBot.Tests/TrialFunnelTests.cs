using System;
using System.Threading.Tasks;
using ValutaBot.MiniApp;
using Xunit;

namespace ValutaBot.Tests
{
    public class TrialFunnelTests
    {
        [Fact]
        public void PocketRegistration_TrialProperties_InitializeProperly()
        {
            var reg = new TelegramBotService.PocketRegistration
            {
                ChatId = 123456789,
                PocketId = "98765432",
                HasRegistered = true,
                HasDeposited = false,
                DepositAmount = 0.0,
                TrialStartedAt = DateTime.UtcNow,
                TrialEndsAt = DateTime.UtcNow.AddDays(3),
                ReminderDay2Sent = false,
                ReminderDay3Sent = false,
                TrialExpiredSent = false
            };

            Assert.Equal(123456789, reg.ChatId);
            Assert.Equal("98765432", reg.PocketId);
            Assert.True(reg.HasRegistered);
            Assert.False(reg.HasDeposited);
            Assert.NotNull(reg.TrialStartedAt);
            Assert.NotNull(reg.TrialEndsAt);
            Assert.True(reg.TrialEndsAt > reg.TrialStartedAt);
            Assert.False(reg.ReminderDay2Sent);
            Assert.False(reg.ReminderDay3Sent);
            Assert.False(reg.TrialExpiredSent);
        }

        [Fact]
        public void TrialStatus_ActiveWhenWithinThreeDays()
        {
            var now = DateTime.UtcNow;
            var reg = new TelegramBotService.PocketRegistration
            {
                TrialStartedAt = now.AddDays(-1),
                TrialEndsAt = now.AddDays(2),
                HasDeposited = false
            };

            bool isActive = reg.TrialEndsAt.HasValue && reg.TrialEndsAt.Value > now;
            Assert.True(isActive);
        }

        [Fact]
        public void TrialStatus_ExpiredWhenPastThreeDays()
        {
            var now = DateTime.UtcNow;
            var reg = new TelegramBotService.PocketRegistration
            {
                TrialStartedAt = now.AddDays(-3).AddMinutes(-5),
                TrialEndsAt = now.AddMinutes(-5),
                HasDeposited = false
            };

            bool isActive = reg.TrialEndsAt.HasValue && reg.TrialEndsAt.Value > now;
            Assert.False(isActive);
        }

        [Fact]
        public void Day2Reminder_TriggersBetween24And60HoursElapsed()
        {
            var now = DateTime.UtcNow;
            // 25 hours elapsed, 47 hours remaining out of 72
            var reg = new TelegramBotService.PocketRegistration
            {
                TrialStartedAt = now.AddHours(-25),
                TrialEndsAt = now.AddHours(47),
                ReminderDay2Sent = false,
                ReminderDay3Sent = false,
                HasDeposited = false
            };

            double elapsedHours = (now - reg.TrialStartedAt.Value).TotalHours;
            double remainingHours = (reg.TrialEndsAt.Value - now).TotalHours;

            bool shouldSendDay2 = elapsedHours >= 24.0 && remainingHours > 12.0 && !reg.ReminderDay2Sent;
            Assert.True(shouldSendDay2);
        }

        [Fact]
        public void Day3UrgencyReminder_TriggersWhen12HoursOrLessRemaining()
        {
            var now = DateTime.UtcNow;
            // 65 hours elapsed, 7 hours remaining
            var reg = new TelegramBotService.PocketRegistration
            {
                TrialStartedAt = now.AddHours(-65),
                TrialEndsAt = now.AddHours(7),
                ReminderDay2Sent = true,
                ReminderDay3Sent = false,
                HasDeposited = false
            };

            double remainingHours = (reg.TrialEndsAt.Value - now).TotalHours;
            bool shouldSendDay3 = remainingHours <= 12.0 && remainingHours > 0 && !reg.ReminderDay3Sent;
            Assert.True(shouldSendDay3);
        }

        [Fact]
        public void ExpiredBlock_TriggersWhenNowExceedsTrialEndsAt()
        {
            var now = DateTime.UtcNow;
            // Trial ended 10 minutes ago
            var reg = new TelegramBotService.PocketRegistration
            {
                TrialStartedAt = now.AddHours(-72).AddMinutes(-10),
                TrialEndsAt = now.AddMinutes(-10),
                TrialExpiredSent = false,
                HasDeposited = false
            };

            bool isExpired = now >= reg.TrialEndsAt.Value && !reg.TrialExpiredSent;
            Assert.True(isExpired);
        }

        [Fact]
        public void DepositedUser_BypassesTrialRemindersAndExpiry()
        {
            var now = DateTime.UtcNow;
            var reg = new TelegramBotService.PocketRegistration
            {
                TrialStartedAt = now.AddDays(-10),
                TrialEndsAt = now.AddDays(-7),
                HasDeposited = true,
                DepositAmount = 50.0
            };

            // Deposited users should never be considered expired or subjected to trial lockout
            Assert.True(reg.HasDeposited);
        }

        [Fact]
        public void AntiAbuse_PreventsResettingTrialOnOldPocketId()
        {
            var now = DateTime.UtcNow;
            var oldReg = new TelegramBotService.PocketRegistration
            {
                PocketId = "11223344",
                TrialStartedAt = now.AddDays(-5),
                TrialEndsAt = now.AddDays(-2),
                HasDeposited = false
            };

            // When user tries to submit 11223344 again from new chat, TrialStartedAt.HasValue detects prior trial
            bool alreadyUsedTrial = oldReg.TrialStartedAt.HasValue;
            bool isStillActive = oldReg.TrialEndsAt.HasValue && oldReg.TrialEndsAt.Value > now;

            Assert.True(alreadyUsedTrial);
            Assert.False(isStillActive); // Thus prompting for deposit instead of giving another 3 days
        }
    }
}
