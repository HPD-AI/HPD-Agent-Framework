//! Streaming Conversations Example
//! 
//! This example demonstrates real-time streaming conversations with event handling.
//! Learn how to process streaming responses and handle different event types.

use hpd_rust_agent::{AgentBuilder, Conversation, AppSettings};
use tokio_stream::StreamExt;
use serde_json::Value;
use std::io::{self, Write};

/// Event processor for handling different streaming event types
struct StreamEventProcessor {
    current_message: String,
    function_calls: Vec<String>,
    step_count: u32,
}

impl StreamEventProcessor {
    fn new() -> Self {
        Self {
            current_message: String::new(),
            function_calls: Vec::new(),
            step_count: 0,
        }
    }

    /// Process a streaming event and return whether to continue
    fn process_event(&mut self, event_json: &str) -> Result<bool, serde_json::Error> {
        let event: Value = serde_json::from_str(event_json)?;
        
        match event["type"].as_str() {
            Some("STEP_STARTED") => {
                self.step_count += 1;
                if let Some(step) = event["step"].as_str() {
                    println!("\n🚀 Step {}: {}", self.step_count, step);
                }
            }
            
            Some("TEXT_MESSAGE_CONTENT") => {
                if let Some(content) = event["content"].as_str() {
                    print!("{}", content);
                    io::stdout().flush().unwrap();
                    self.current_message.push_str(content);
                }
            }
            
            Some("FUNCTION_CALL_STARTED") => {
                if let Some(function_name) = event["function_name"].as_str() {
                    println!("\n🔧 Calling function: {}", function_name);
                    if let Some(args) = event["arguments"].as_object() {
                        println!("   📋 Arguments: {}", serde_json::to_string_pretty(args)?);
                    }
                    self.function_calls.push(function_name.to_string());
                }
            }
            
            Some("FUNCTION_CALL_RESULT") => {
                if let Some(function_name) = event["function_name"].as_str() {
                    println!("\n✅ Function {} completed", function_name);
                    if let Some(result) = event["result"].as_str() {
                        // Try to parse result as JSON for pretty printing
                        match serde_json::from_str::<Value>(result) {
                            Ok(json_result) => {
                                println!("   📤 Result: {}", serde_json::to_string_pretty(&json_result)?);
                            }
                            Err(_) => {
                                println!("   📤 Result: {}", result);
                            }
                        }
                    }
                }
            }
            
            Some("STEP_COMPLETED") => {
                if let Some(step) = event["step"].as_str() {
                    println!("\n✅ Completed: {}", step);
                }
            }
            
            Some("CONVERSATION_ENDED") => {
                println!("\n🏁 Conversation ended");
                return Ok(false); // Stop processing
            }
            
            Some("ERROR") => {
                if let Some(error) = event["error"].as_str() {
                    eprintln!("\n❌ Error: {}", error);
                }
                return Ok(false); // Stop on error
            }
            
            _ => {
                // Unknown event type - just log it
                println!("\n🔍 Unknown event: {}", event_json);
            }
        }
        
        Ok(true) // Continue processing
    }

    /// Get summary of the streaming session
    fn get_summary(&self) -> String {
        format!(
            "Session Summary:\n• Steps processed: {}\n• Function calls: {}\n• Message length: {} characters",
            self.step_count,
            self.function_calls.len(),
            self.current_message.len()
        )
    }
}

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    println!("📡 HPD Rust Agent Library - Streaming Conversations Example");
    println!("============================================================");

    // Step 1: Load configuration
    println!("\n📄 Loading configuration...");
    let config = AppSettings::load()
        .map_err(|e| format!("Failed to load configuration: {}", e))?;
    
    let api_key = config.get_openrouter_api_key()
        .ok_or("❌ OpenRouter API key not found in configuration")?;
    
    let model = config.get_default_model()
        .unwrap_or("google/gemini-2.5-pro");
    
    println!("✅ Configuration loaded successfully");

    // Step 2: Create an agent optimized for streaming
    println!("\n🤖 Creating streaming-optimized agent...");
    let agent = AgentBuilder::new("streaming-assistant")
        .with_instructions(
            "You are a helpful assistant that provides detailed, step-by-step responses. \
             When explaining complex topics, break them down into clear steps. \
             Use available tools when helpful to demonstrate functionality."
        )
        .with_max_function_calls(15)
        .with_max_conversation_history(20)
        .with_openrouter(model, api_key)
        .with_registered_plugins() // Include all available plugins
        .build()
        .map_err(|e| format!("Failed to create agent: {}", e))?;
    
    println!("✅ Agent created successfully");

    // Step 3: Create conversation
    let conversation = Conversation::new(vec![agent])
        .map_err(|e| format!("Failed to create conversation: {}", e))?;

    // Step 4: Demonstrate different streaming scenarios
    let streaming_examples = vec![
        (
            "Quick Response", 
            "What is 2 + 2? Give me a brief answer."
        ),
        (
            "Detailed Explanation",
            "Explain how machine learning works, including the key concepts and steps involved."
        ),
        (
            "Tool Usage",
            "Calculate the compound interest on $5000 invested at 4% annual interest for 15 years, compounded monthly. Show your work."
        ),
        (
            "Complex Analysis",
            "Compare the advantages and disadvantages of different programming languages for web development, considering factors like performance, ease of learning, and ecosystem."
        ),
    ];

    for (i, (title, message)) in streaming_examples.iter().enumerate() {
        println!("\n{}", "=".repeat(80));
        println!("🎯 Streaming Example {}: {}", i + 1, title);
        println!("{}", "=".repeat(80));
        println!("📤 Sending: {}", message);
        println!("{}", "─".repeat(60));

        // Create event processor for this conversation
        let mut processor = StreamEventProcessor::new();

        // Start streaming
        match conversation.send_streaming(message) {
            Ok(mut stream) => {
                let mut event_count = 0;
                let start_time = std::time::Instant::now();

                // Process events as they arrive
                while let Some(event_json) = stream.next().await {
                    event_count += 1;
                    
                    match processor.process_event(&event_json) {
                        Ok(should_continue) => {
                            if !should_continue {
                                break;
                            }
                        }
                        Err(e) => {
                            eprintln!("\n❌ Error processing event: {}", e);
                            eprintln!("   Raw event: {}", event_json);
                        }
                    }

                    // Safety check to prevent infinite loops
                    if event_count > 100 {
                        println!("\n⚠️  Reached maximum event count, stopping stream");
                        break;
                    }
                }

                let duration = start_time.elapsed();
                println!("\n\n📊 Streaming Statistics:");
                println!("   • Duration: {:.2} seconds", duration.as_secs_f64());
                println!("   • Events processed: {}", event_count);
                println!("   • {}", processor.get_summary());

            }
            Err(e) => {
                eprintln!("❌ Error starting stream: {}", e);
            }
        }

        // Pause between examples
        if i < streaming_examples.len() - 1 {
            println!("\n⏸️  Pausing before next example...");
            tokio::time::sleep(tokio::time::Duration::from_millis(2000)).await;
        }
    }

    // Step 5: Interactive streaming demo (optional)
    println!("\n{}", "=".repeat(80));
    println!("🎮 Interactive Streaming Demo");
    println!("{}", "=".repeat(80));
    println!("💡 This would be an interactive session in a real application.");
    println!("   Users could type messages and see real-time responses.");
    println!("   For safety in this example, we're using predefined messages.");

    // Simulate interactive conversation
    let interactive_messages = vec![
        "Hello! How are you today?",
        "Can you help me understand the difference between async and sync programming?",
        "That's helpful! Can you show me a simple example?",
        "Thanks! You're very helpful.",
    ];

    for (i, message) in interactive_messages.iter().enumerate() {
        println!("\n👤 User: {}", message);
        println!("🤖 Assistant: ");

        let mut processor = StreamEventProcessor::new();
        match conversation.send_streaming(message) {
            Ok(mut stream) => {
                while let Some(event_json) = stream.next().await {
                    if !processor.process_event(&event_json).unwrap_or(false) {
                        break;
                    }
                }
                println!(); // New line after response
            }
            Err(e) => {
                eprintln!("❌ Error: {}", e);
            }
        }

        // Short pause between messages
        tokio::time::sleep(tokio::time::Duration::from_millis(1000)).await;
    }

    println!("\n✨ Streaming conversations example completed!");
    println!("\n🎯 Key Features Demonstrated:");
    println!("   • Real-time event processing with tokio-stream");
    println!("   • Multiple event types (STEP_STARTED, TEXT_MESSAGE_CONTENT, etc.)");
    println!("   • Function call streaming with arguments and results");
    println!("   • Error handling in streaming contexts");
    println!("   • Performance monitoring and statistics");
    println!("   • Interactive conversation patterns");
    println!("   • Graceful stream termination");

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn test_event_processor() {
        let mut processor = StreamEventProcessor::new();

        // Test text content event
        let text_event = r#"{"type": "TEXT_MESSAGE_CONTENT", "content": "Hello"}"#;
        let result = processor.process_event(text_event);
        assert!(result.is_ok());
        assert!(result.unwrap());
        assert_eq!(processor.current_message, "Hello");

        // Test function call event
        let func_event = r#"{"type": "FUNCTION_CALL_STARTED", "function_name": "test_func", "arguments": {"x": 1}}"#;
        let result = processor.process_event(func_event);
        assert!(result.is_ok());
        assert!(result.unwrap());
        assert_eq!(processor.function_calls.len(), 1);
        assert_eq!(processor.function_calls[0], "test_func");

        // Test step started event
        let step_event = r#"{"type": "STEP_STARTED", "step": "Processing"}"#;
        let result = processor.process_event(step_event);
        assert!(result.is_ok());
        assert!(result.unwrap());
        assert_eq!(processor.step_count, 1);
    }

    #[tokio::test]
    async fn test_streaming_with_real_agent() {
        // Only run if configuration is available
        if let Ok(config) = AppSettings::load() {
            if let Some(api_key) = config.get_openrouter_api_key() {
                let agent = AgentBuilder::new("test-streaming")
                    .with_instructions("Give brief responses")
                    .with_openrouter("google/gemini-2.5-pro", api_key)
                    .build()
                    .expect("Agent should build");

                let conversation = Conversation::new(vec![agent])
                    .expect("Conversation should create");

                let mut stream = conversation.send_streaming("Say hello briefly")
                    .expect("Stream should start");

                let mut received_events = 0;
                while let Some(_event) = stream.next().await {
                    received_events += 1;
                    if received_events > 10 {
                        break; // Prevent infinite test
                    }
                }

                assert!(received_events > 0, "Should receive at least one event");
            }
        }
    }
}
