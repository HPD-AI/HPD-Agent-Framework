using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using HPD.Agent;
using Microsoft.Extensions.AI;
using Xunit;

namespace HPD.Agent.Tests.Core;

/// <summary>
/// Unit tests for HistoryReductionState class.
/// Tests cache validation, integrity checking, and message transformation.
/// </summary>
public class HistoryReductionStateTests
{
    #region Test Data Helpers

    /// <summary>
    /// Creates a list of test messages with sequential content.
    /// </summary>
    private static List<ChatMessage> CreateTestMessages(int count)
    {
        var messages = new List<ChatMessage>();
        for (int i = 0; i < count; i++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"Message {i}"));
        }
        return messages;
    }

    /// <summary>
    /// Creates a sample CachedReduction for testing.
    /// </summary>
    private static CachedReduction CreateSampleReduction(
        List<ChatMessage> messages,
        int summarizedUpToIndex = 90,
        int targetMessageCount = 20,
        int reductionThreshold = 5)
    {
        return CachedReduction.Create(
            messages,
            "Summary of old messages",
            summarizedUpToIndex,
            targetMessageCount,
            reductionThreshold);
    }

    #endregion

    #region Factory Method Tests

    [Fact]
    public void Create_WithValidParameters_ShouldCreateReduction()
    {
        // Arrange
        var messages = CreateTestMessages(100);
        var summaryContent = "Summary of messages 0-89";
        var summarizedUpToIndex = 90;
        var targetMessageCount = 20;
        var reductionThreshold = 5;

        // Act
        var reduction = CachedReduction.Create(
            messages,
            summaryContent,
            summarizedUpToIndex,
            targetMessageCount,
            reductionThreshold);

        // Assert
        reduction.Should().NotBeNull();
        reduction.SummarizedUpToIndex.Should().Be(summarizedUpToIndex);
        reduction.MessageCountAtReduction.Should().Be(messages.Count);
        reduction.SummaryContent.Should().Be(summaryContent);
        reduction.TargetMessageCount.Should().Be(targetMessageCount);
        reduction.ReductionThreshold.Should().Be(reductionThreshold);
        reduction.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        reduction.MessageHash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Create_WithEmptyMessages_ShouldStillWork()
    {
        // Arrange
        var messages = new List<ChatMessage>();

        // Act
        var reduction = CachedReduction.Create(
            messages,
            "Empty summary",
            0,
            20,
            5);

        // Assert
        reduction.Should().NotBeNull();
        reduction.MessageCountAtReduction.Should().Be(0);
        reduction.SummarizedUpToIndex.Should().Be(0);
    }

    #endregion

    #region IsValidFor Tests

    [Fact]
    public void IsValidFor_WithSameMessageCount_ShouldReturnTrue()
    {
        // Arrange
        var messages = CreateTestMessages(100);
        var reduction = CreateSampleReduction(messages);

        // Act
        var isValid = reduction.IsValidFor(100);

        // Assert
        isValid.Should().BeTrue("no messages added or removed");
    }

    [Fact]
    public void IsValidFor_WithNewMessagesWithinThreshold_ShouldReturnTrue()
    {
        // Arrange
        var messages = CreateTestMessages(100);
        var reduction = CreateSampleReduction(messages, targetMessageCount: 20, reductionThreshold: 5);

        // Act - Add 4 new messages (within threshold of 5)
        var isValid = reduction.IsValidFor(104);

        // Assert
        isValid.Should().BeTrue("new messages (4) are within threshold (5)");
    }

    [Fact]
    public void IsValidFor_WithNewMessagesExceedingThreshold_ShouldReturnFalse()
    {
        // Arrange
        var messages = CreateTestMessages(100);
        var reduction = CreateSampleReduction(messages, targetMessageCount: 20, reductionThreshold: 5);

        // Act - Add 6 new messages (exceeds threshold of 5)
        var isValid = reduction.IsValidFor(106);

        // Assert
        isValid.Should().BeFalse("new messages (6) exceed threshold (5)");
    }

    [Fact]
    public void IsValidFor_WithFewerMessages_ShouldReturnFalse()
    {
        // Arrange
        var messages = CreateTestMessages(100);
        var reduction = CreateSampleReduction(messages);

        // Act - Messages deleted
        var isValid = reduction.IsValidFor(95);

        // Assert
        isValid.Should().BeFalse("messages were deleted (invalidates cache)");
    }

    [Fact]
    public void IsValidFor_WithZeroThreshold_ShouldRequireExactCount()
    {
        // Arrange
        var messages = CreateTestMessages(100);
        var reduction = CreateSampleReduction(messages, reductionThreshold: 0);

        // Act
        var validSame = reduction.IsValidFor(100);
        var invalidMore = reduction.IsValidFor(101);

        // Assert
        validSame.Should().BeTrue();
        invalidMore.Should().BeFalse("threshold is 0, requires exact count");
    }

    [Fact]
    public void IsValidFor_WithLargeThreshold_ShouldAllowManyNewMessages()
    {
        // Arrange
        var messages = CreateTestMessages(100);
        var reduction = CreateSampleReduction(messages, reductionThreshold: 50);

        // Act
        var validManyMessages = reduction.IsValidFor(150);
        var invalidTooMany = reduction.IsValidFor(151);

        // Assert
        validManyMessages.Should().BeTrue("50 new messages are within threshold (50)");
        invalidTooMany.Should().BeFalse("51 new messages exceed threshold (50)");
    }

    #endregion

    #region ValidateIntegrity Tests

    [Fact]
    public void ValidateIntegrity_WithUnchangedMessages_ShouldReturnTrue()
    {
        // Arrange
        var messages = CreateTestMessages(100);
        var reduction = CreateSampleReduction(messages, summarizedUpToIndex: 90);

        // Act
        var isValid = reduction.ValidateIntegrity(messages);

        // Assert
        isValid.Should().BeTrue("messages have not changed");
    }

    [Fact]
    public void ValidateIntegrity_WithModifiedMessage_ShouldReturnFalse()
    {
        // Arrange
        var messages = CreateTestMessages(100);
        var reduction = CreateSampleReduction(messages, summarizedUpToIndex: 90);

        // Act - Modify a message in the summarized range
        var modifiedMessages = messages.ToList();
        modifiedMessages[50] = new ChatMessage(ChatRole.User, "MODIFIED MESSAGE");

        var isValid = reduction.ValidateIntegrity(modifiedMessages);

        // Assert
        isValid.Should().BeFalse("message content changed");
    }

    [Fact]
    public void ValidateIntegrity_WithReorderedMessages_ShouldReturnFalse()
    {
        // Arrange
        var messages = CreateTestMessages(100);
        var reduction = CreateSampleReduction(messages, summarizedUpToIndex: 90);

        // Act - Swap two messages
        var reorderedMessages = messages.ToList();
        (reorderedMessages[10], reorderedMessages[20]) = (reorderedMessages[20], reorderedMessages[10]);

        var isValid = reduction.ValidateIntegrity(reorderedMessages);

        // Assert
        isValid.Should().BeFalse("message order changed");
    }

    [Fact]
    public void ValidateIntegrity_WithDeletedMessages_ShouldReturnFalse()
    {
        // Arrange
        var messages = CreateTestMessages(100);
        var reduction = CreateSampleReduction(messages, summarizedUpToIndex: 90);

        // Act - Remove a message
        var deletedMessages = messages.Take(50).Concat(messages.Skip(51)).ToList();

        var isValid = reduction.ValidateIntegrity(deletedMessages);

        // Assert
        isValid.Should().BeFalse("messages were deleted");
    }

    [Fact]
    public void ValidateIntegrity_WithNewMessagesAppended_ShouldReturnTrue()
    {
        // Arrange
        var messages = CreateTestMessages(100);
        var reduction = CreateSampleReduction(messages, summarizedUpToIndex: 90);

        // Act - Add new messages AFTER the summarized range
        var newMessages = messages.ToList();
        newMessages.Add(new ChatMessage(ChatRole.User, "New message 100"));
        newMessages.Add(new ChatMessage(ChatRole.User, "New message 101"));

        var isValid = reduction.ValidateIntegrity(newMessages);

        // Assert
        isValid.Should().BeTrue("new messages appended, but old messages unchanged");
    }

    [Fact]
    public void ValidateIntegrity_WithFewerMessagesThanSummarized_ShouldThrow()
    {
        // Arrange
        var messages = CreateTestMessages(100);
        var reduction = CreateSampleReduction(messages, summarizedUpToIndex: 90);

        // Act - Provide fewer messages than summarized index
        var tooFewMessages = messages.Take(50).ToList();

        var act = () => reduction.ValidateIntegrity(tooFewMessages);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Message count*less than SummarizedUpToIndex*");
    }

    #endregion

    #region ApplyToMessages Tests

    [Fact]
    public void ApplyToMessages_WithoutSystemMessage_ShouldReturnSummaryAndRecentMessages()
    {
        // Arrange
        var messages = CreateTestMessages(100);
        var reduction = CreateSampleReduction(messages, summarizedUpToIndex: 90);

        // Act
        var reduced = reduction.ApplyToMessages(messages).ToList();

        // Assert
        reduced.Should().HaveCount(11); // 1 summary + 10 recent (90-99)
        reduced[0].Role.Should().Be(ChatRole.Assistant);
        reduced[0].Text.Should().Be("Summary of old messages");
        reduced[1].Text.Should().Be("Message 90");
        reduced[10].Text.Should().Be("Message 99");
    }

    [Fact]
    public void ApplyToMessages_WithSystemMessage_ShouldPrependSystem()
    {
        // Arrange
        var messages = CreateTestMessages(100);
        var reduction = CreateSampleReduction(messages, summarizedUpToIndex: 90);
        var systemMessage = new ChatMessage(ChatRole.System, "You are a helpful assistant");

        // Act
        var reduced = reduction.ApplyToMessages(messages, systemMessage).ToList();

        // Assert
        reduced.Should().HaveCount(12); // 1 system + 1 summary + 10 recent
        reduced[0].Role.Should().Be(ChatRole.System);
        reduced[0].Text.Should().Be("You are a helpful assistant");
        reduced[1].Role.Should().Be(ChatRole.Assistant);
        reduced[1].Text.Should().Be("Summary of old messages");
        reduced[2].Text.Should().Be("Message 90");
    }

    [Fact]
    public void ApplyToMessages_WithModifiedMessages_ShouldThrow()
    {
        // Arrange
        var messages = CreateTestMessages(100);
        var reduction = CreateSampleReduction(messages, summarizedUpToIndex: 90);

        // Modify a message in the summarized range
        var modifiedMessages = messages.ToList();
        modifiedMessages[50] = new ChatMessage(ChatRole.User, "MODIFIED");

        // Act
        var act = () => reduction.ApplyToMessages(modifiedMessages).ToList();

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*integrity check failed*");
    }

    [Fact]
    public void ApplyToMessages_WithNewMessagesAppended_ShouldIncludeThem()
    {
        // Arrange
        var messages = CreateTestMessages(100);
        var reduction = CreateSampleReduction(messages, summarizedUpToIndex: 90);

        // Add new messages
        var newMessages = messages.ToList();
        newMessages.Add(new ChatMessage(ChatRole.User, "Message 100"));
        newMessages.Add(new ChatMessage(ChatRole.User, "Message 101"));

        // Act
        var reduced = reduction.ApplyToMessages(newMessages).ToList();

        // Assert
        reduced.Should().HaveCount(13); // 1 summary + 12 recent (90-101)
        reduced[0].Text.Should().Be("Summary of old messages");
        reduced[11].Text.Should().Be("Message 100");
        reduced[12].Text.Should().Be("Message 101");
    }

    [Fact]
    public void ApplyToMessages_WithAllMessagesSummarized_ShouldReturnOnlySummary()
    {
        // Arrange
        var messages = CreateTestMessages(100);
        var reduction = CreateSampleReduction(messages, summarizedUpToIndex: 100); // All summarized

        // Act
        var reduced = reduction.ApplyToMessages(messages).ToList();

        // Assert
        reduced.Should().HaveCount(1); // Only summary
        reduced[0].Text.Should().Be("Summary of old messages");
    }

    #endregion

    #region Immutability Tests

    [Fact]
    public void HistoryReductionState_ShouldBeImmutable()
    {
        // Arrange
        var messages = CreateTestMessages(100);
        var reduction = CreateSampleReduction(messages);

        // Act - Try to modify (should use 'with' expression, not mutation)
        var modified = reduction with { SummaryContent = "New summary" };

        // Assert
        reduction.SummaryContent.Should().Be("Summary of old messages", "original unchanged");
        modified.SummaryContent.Should().Be("New summary", "new instance created");
        reduction.Should().NotBeSameAs(modified, "different instances");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Create_WithNullSummaryContent_ShouldNotThrow()
    {
        // Arrange
        var messages = CreateTestMessages(10);

        // Act
        var act = () => CachedReduction.Create(
            messages,
            null!, // Null summary
            5,
            20,
            5);

        // Assert - Should not throw, but will fail on ApplyToMessages
        act.Should().NotThrow("Create should accept null summary");
    }

    [Fact]
    public void Create_WithEmptySummaryContent_ShouldWork()
    {
        // Arrange
        var messages = CreateTestMessages(10);

        // Act
        var reduction = CachedReduction.Create(
            messages,
            "", // Empty summary
            5,
            20,
            5);

        // Assert
        reduction.SummaryContent.Should().BeEmpty();
    }

    [Fact]
    public void ApplyToMessages_WithEmptyMessageList_ShouldHandleGracefully()
    {
        // Arrange
        var messages = new List<ChatMessage>();
        var reduction = CachedReduction.Create(messages, "Empty", 0, 20, 5);

        // Act
        var reduced = reduction.ApplyToMessages(messages).ToList();

        // Assert
        reduced.Should().HaveCount(1); // Just the summary
        reduced[0].Text.Should().Be("Empty");
    }

    [Fact]
    public void ValidateIntegrity_WithZeroSummarizedIndex_ShouldReturnTrue()
    {
        // Arrange
        var messages = CreateTestMessages(10);
        var reduction = CachedReduction.Create(messages, "Summary", 0, 20, 5);

        // Act
        var isValid = reduction.ValidateIntegrity(messages);

        // Assert
        isValid.Should().BeTrue("no messages to validate when index is 0");
    }

    #endregion

    #region Hash Consistency Tests

    [Fact]
    public void MessageHash_ShouldBeConsistent_ForSameMessages()
    {
        // Arrange
        var messages1 = CreateTestMessages(100);
        var messages2 = CreateTestMessages(100); // Same content, different instances

        // Act
        var reduction1 = CreateSampleReduction(messages1);
        var reduction2 = CreateSampleReduction(messages2);

        // Assert
        reduction1.MessageHash.Should().Be(reduction2.MessageHash,
            "same message content should produce same hash");
    }

    [Fact]
    public void MessageHash_ShouldBeDifferent_ForDifferentMessages()
    {
        // Arrange
        var messages1 = CreateTestMessages(100);
        var messages2 = CreateTestMessages(100);
        messages2[50] = new ChatMessage(ChatRole.User, "DIFFERENT");

        // Act
        var reduction1 = CreateSampleReduction(messages1);
        var reduction2 = CreateSampleReduction(messages2);

        // Assert
        reduction1.MessageHash.Should().NotBe(reduction2.MessageHash,
            "different message content should produce different hash");
    }

    [Fact]
    public void MessageHash_ShouldBeDifferent_WhenOrderChanges()
    {
        // Arrange
        var messages1 = CreateTestMessages(100);
        var messages2 = messages1.ToList();
        (messages2[10], messages2[20]) = (messages2[20], messages2[10]);

        // Act
        var reduction1 = CreateSampleReduction(messages1);
        var reduction2 = CreateSampleReduction(messages2);

        // Assert
        reduction1.MessageHash.Should().NotBe(reduction2.MessageHash,
            "message order change should produce different hash");
    }

    #endregion

    #region Integration Scenarios

    [Fact]
    public void Scenario_CacheHitAcrossMultipleTurns()
    {
        // Arrange - Turn 1: Create reduction
        var messages = CreateTestMessages(100);
        var reduction = CreateSampleReduction(messages, reductionThreshold: 10);

        // Act - Turn 2: Add 5 new messages
        var turn2Messages = messages.ToList();
        for (int i = 100; i < 105; i++)
        {
            turn2Messages.Add(new ChatMessage(ChatRole.User, $"Message {i}"));
        }

        // Assert - Cache should be valid
        reduction.IsValidFor(turn2Messages.Count).Should().BeTrue();
        reduction.ValidateIntegrity(turn2Messages).Should().BeTrue();

        // Can apply reduction successfully
        var reduced = reduction.ApplyToMessages(turn2Messages).ToList();
        reduced.Should().HaveCount(16); // 1 summary + 15 recent (90-104)
    }

    [Fact]
    public void Scenario_CacheMissWhenTooManyNewMessages()
    {
        // Arrange - Turn 1: Create reduction
        var messages = CreateTestMessages(100);
        var reduction = CreateSampleReduction(messages, reductionThreshold: 5);

        // Act - Turn 2: Add 10 new messages (exceeds threshold)
        var turn2Messages = messages.ToList();
        for (int i = 100; i < 110; i++)
        {
            turn2Messages.Add(new ChatMessage(ChatRole.User, $"Message {i}"));
        }

        // Assert - Cache should be invalid
        reduction.IsValidFor(turn2Messages.Count).Should().BeFalse(
            "too many new messages added");

        // Need to create new reduction
        var newReduction = CreateSampleReduction(turn2Messages, summarizedUpToIndex: 100);
        newReduction.IsValidFor(turn2Messages.Count).Should().BeTrue();
    }

    [Fact]
    public void Scenario_SerializationRoundTrip()
    {
        // Arrange
        var messages = CreateTestMessages(100);
        var original = CreateSampleReduction(messages);

        // Act - Simulate serialization via 'with' (immutable record)
        var copy = original with { }; // Identity copy

        // Assert - All properties should match
        copy.SummarizedUpToIndex.Should().Be(original.SummarizedUpToIndex);
        copy.MessageCountAtReduction.Should().Be(original.MessageCountAtReduction);
        copy.SummaryContent.Should().Be(original.SummaryContent);
        copy.CreatedAt.Should().Be(original.CreatedAt);
        copy.MessageHash.Should().Be(original.MessageHash);
        copy.TargetMessageCount.Should().Be(original.TargetMessageCount);
        copy.ReductionThreshold.Should().Be(original.ReductionThreshold);
    }

    #endregion
}
