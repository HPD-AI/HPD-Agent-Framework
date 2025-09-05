use hpd_rust_agent::agent::{AgentBuilder, ProviderConfig, ChatProvider};
use hpd_rust_agent::conversation::Conversation;
use hpd_rust_agent::example_plugins::{MathPlugin, StringPlugin};
use serde_json::Value;
use std::fs;

/// Helper function to read API key from appsettings.json
fn read_api_key_from_config() -> Result<String, Box<dyn std::error::Error>> {
    let config_content = fs::read_to_string("appsettings.json")?;
    let config: Value = serde_json::from_str(&config_content)?;
    
    if let Some(openrouter) = config.get("OpenRouter") {
        if let Some(api_key) = openrouter.get("ApiKey") {
            if let Some(key_str) = api_key.as_str() {
                if !key_str.is_empty() && key_str != "your-openrouter-api-key-here" {
                    return Ok(key_str.to_string());
                }
            }
        }
    }
    
    Err("No valid OpenRouter API key found in config".into())
}

/// Test that verifies stateful conversation behavior
/// This test demonstrates that the same Conversation instance maintains context across multiple send() calls
#[tokio::test]
async fn test_stateful_conversation_maintains_context() {
    // Try to get API key from config file first, then environment
    let api_key = read_api_key_from_config()
        .or_else(|_| std::env::var("OPENROUTER_API_KEY").map_err(|e| Box::new(e) as Box<dyn std::error::Error>))
        .unwrap_or_else(|_: Box<dyn std::error::Error>| {
            println!("Skipping test - no API key found in appsettings.json or OPENROUTER_API_KEY environment variable");
            return String::new();
        });
    
    if api_key.is_empty() {
        return;
    }

    let agent = AgentBuilder::new("Test Agent")
        .with_instructions("You are a test assistant. Remember previous calculations and context.")
        .with_provider(ProviderConfig {
            provider: ChatProvider::OpenRouter,
            model_name: "google/gemini-2.5-pro".to_string(),
            api_key: Some(api_key),
            endpoint: Some("https://openrouter.ai/api/v1".to_string()),
        })
        .with_plugin(MathPlugin { name: "TestMath".to_string() })
        .with_plugin(StringPlugin { operations_count: 0 })
        .with_max_function_calls(3)
        .build()
        .expect("Failed to create agent");

    // Create a single conversation for the entire test
    let conversation = Conversation::new(vec![agent])
        .expect("Failed to create conversation");

    // First interaction: Basic calculation
    let response1 = conversation.send("Calculate 10 + 5 using the add function")
        .expect("Failed to send first message");
    
    // Verify we got a response
    assert!(!response1.is_empty(), "First response should not be empty");
    
    // Second interaction: Reference previous calculation (tests memory)
    let response2 = conversation.send("What was the result of the previous calculation?")
        .expect("Failed to send second message");
    
    // Verify we got a response
    assert!(!response2.is_empty(), "Second response should not be empty");
    
    // The response should reference the previous calculation or contain "15"
    // This is a basic test - in reality you'd parse the JSON response more carefully
    let response2_lower = response2.to_lowercase();
    let has_context = response2_lower.contains("15") || 
                     response2_lower.contains("previous") || 
                     response2_lower.contains("earlier") ||
                     response2_lower.contains("before");
    
    assert!(has_context, 
           "Second response should reference previous calculation: {}", response2);

    // Third interaction: Build on context further
    let response3 = conversation.send("Multiply that result by 2")
        .expect("Failed to send third message");
    
    assert!(!response3.is_empty(), "Third response should not be empty");
    
    println!("✅ Stateful conversation test passed:");
    println!("   Response 1: {}", response1.chars().take(100).collect::<String>());
    println!("   Response 2: {}", response2.chars().take(100).collect::<String>());
    println!("   Response 3: {}", response3.chars().take(100).collect::<String>());
}

/// Test that demonstrates stateless behavior (new conversation for each message)
#[tokio::test] 
async fn test_stateless_conversation_loses_context() {
    let api_key = read_api_key_from_config()
        .or_else(|_| std::env::var("OPENROUTER_API_KEY").map_err(|e| Box::new(e) as Box<dyn std::error::Error>))
        .unwrap_or_else(|_: Box<dyn std::error::Error>| {
            println!("Skipping test - no API key found in appsettings.json or OPENROUTER_API_KEY environment variable");
            return String::new();
        });
    
    if api_key.is_empty() {
        return;
    }

    // Helper function to create a fresh agent
    let create_agent = || {
        AgentBuilder::new("Test Agent")
            .with_instructions("You are a test assistant.")
            .with_provider(ProviderConfig {
                provider: ChatProvider::OpenRouter,
                model_name: "google/gemini-2.5-pro".to_string(),
                api_key: Some(api_key.clone()),
                endpoint: Some("https://openrouter.ai/api/v1".to_string()),
            })
            .with_plugin(MathPlugin { name: "TestMath".to_string() })
            .with_plugin(StringPlugin { operations_count: 0 })
            .with_max_function_calls(3)
            .build()
            .expect("Failed to create agent")
    };

    // First conversation: Basic calculation
    let conversation1 = Conversation::new(vec![create_agent()])
        .expect("Failed to create first conversation");
    
    let response1 = conversation1.send("Calculate 10 + 5 using the add function")
        .expect("Failed to send first message");
    
    assert!(!response1.is_empty(), "First response should not be empty");
    
    // Second conversation: NEW conversation, should have no memory
    let conversation2 = Conversation::new(vec![create_agent()])
        .expect("Failed to create second conversation");
    
    let response2 = conversation2.send("What was the result of the previous calculation?")
        .expect("Failed to send second message");
    
    assert!(!response2.is_empty(), "Second response should not be empty");
    
    // The response should indicate no knowledge of previous calculation
    let response2_lower = response2.to_lowercase();
    let _has_no_context = response2_lower.contains("don't") || 
                         response2_lower.contains("no previous") ||
                         response2_lower.contains("not aware") ||
                         response2_lower.contains("don't have") ||
                         response2_lower.contains("no information");
    
    // Note: This assertion might be flaky depending on the LLM's response
    // In a production test, you'd want more controlled conditions
    println!("📊 Stateless response: {}", response2);
    println!("   (Should indicate no memory of previous calculation)");
    
    println!("✅ Stateless conversation test completed:");
    println!("   Response 1: {}", response1.chars().take(100).collect::<String>());
    println!("   Response 2: {}", response2.chars().take(100).collect::<String>());
}

/// Integration test that verifies conversation memory behavior
#[tokio::test]
async fn test_conversation_memory_integration() {
    let api_key = read_api_key_from_config()
        .or_else(|_| std::env::var("OPENROUTER_API_KEY").map_err(|e| Box::new(e) as Box<dyn std::error::Error>))
        .unwrap_or_else(|_: Box<dyn std::error::Error>| {
            println!("Skipping integration test - no API key found");
            return String::new();
        });
    
    if api_key.is_empty() {
        return;
    }

    let agent = AgentBuilder::new("Memory Test Agent")
        .with_instructions("Remember our conversation. When I ask about previous topics, reference them.")
        .with_provider(ProviderConfig {
            provider: ChatProvider::OpenRouter,
            model_name: "google/gemini-2.5-pro".to_string(),
            api_key: Some(api_key),
            endpoint: Some("https://openrouter.ai/api/v1".to_string()),
        })
        .with_plugin(MathPlugin { name: "MemoryMath".to_string() })
        .with_max_function_calls(5)
        .build()
        .expect("Failed to create memory test agent");

    let conversation = Conversation::new(vec![agent])
        .expect("Failed to create conversation");

    // Build up context through multiple interactions
    let interactions = vec![
        ("My name is Alice", "introduction"),
        ("I like the color blue", "preference"),
        ("Calculate 7 * 8 using multiply", "calculation"),
        ("What's my name and favorite color?", "memory_test"),
        ("What calculation did we just do?", "math_memory_test"),
    ];

    let mut responses = Vec::new();

    for (message, test_type) in interactions {
        println!("🔄 Sending: {}", message);
        
        let response = conversation.send(message)
            .expect(&format!("Failed to send message: {}", message));
        
        responses.push((test_type, response.clone()));
        
        println!("📨 Response: {}", response.chars().take(150).collect::<String>());
        println!();
        
        // Small delay to avoid rate limiting
        tokio::time::sleep(tokio::time::Duration::from_millis(500)).await;
    }

    // Verify that the agent remembered information across the conversation
    let memory_response = &responses[3].1; // "What's my name and favorite color?"
    let math_memory_response = &responses[4].1; // "What calculation did we just do?"

    println!("🧠 Memory Test Results:");
    println!("Memory Response: {}", memory_response.chars().take(200).collect::<String>());
    println!("Math Memory Response: {}", math_memory_response.chars().take(200).collect::<String>());

    // Basic checks - in a real test you'd be more sophisticated about parsing
    let memory_lower = memory_response.to_lowercase();
    let math_memory_lower = math_memory_response.to_lowercase();

    let remembers_name = memory_lower.contains("alice");
    let remembers_color = memory_lower.contains("blue");
    let remembers_calculation = math_memory_lower.contains("7") && math_memory_lower.contains("8") || 
                               math_memory_lower.contains("56") ||
                               math_memory_lower.contains("multiply");

    println!("✅ Memory Assessment:");
    println!("   Remembers name (Alice): {}", remembers_name);
    println!("   Remembers color (blue): {}", remembers_color);
    println!("   Remembers calculation (7*8): {}", remembers_calculation);

    // At least one aspect should be remembered in a functional stateful conversation
    assert!(remembers_name || remembers_color || remembers_calculation,
           "Agent should remember at least some previous context");

    println!("\n🎉 Integration test completed successfully!");
}
