use hpd_rust_agent::agent::{AgentBuilder, ProviderConfig, ChatProvider};
use hpd_rust_agent::conversation::Conversation;
use hpd_rust_agent::example_plugins::{MathPlugin, StringPlugin};
use serde_json::Value;
use std::fs;
use std::time::{Duration, Instant};
use tokio::time::timeout;
use futures_util::StreamExt;

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

/// Helper function to process streaming events and collect text chunks
async fn collect_streaming_response(
    conversation: &Conversation,
    message: &str,
    timeout_secs: u64,
) -> Result<(Vec<String>, String), String> {
    let mut chunks = Vec::new();
    let mut final_response = String::new();
    
    let stream_result = timeout(
        Duration::from_secs(timeout_secs),
        async {
            let mut stream = conversation.send_streaming(message)?;
            
            while let Some(event_json) = stream.next().await {
                chunks.push(event_json.clone());
                
                // Try to parse the JSON to extract text content
                if let Ok(event) = serde_json::from_str::<Value>(&event_json) {
                    if let Some(event_type) = event.get("type").and_then(|t| t.as_str()) {
                        match event_type {
                            "TEXT_MESSAGE_CONTENT" => {
                                if let Some(content) = event.get("content").and_then(|t| t.as_str()) {
                                    final_response.push_str(content);
                                    print!("{}", content);
                                    std::io::Write::flush(&mut std::io::stdout()).unwrap();
                                }
                            }
                            "STEP_COMPLETED" | "CONVERSATION_ENDED" => {
                                println!("\n✅ Streaming completed");
                                break;
                            }
                            "ERROR" => {
                                let error_msg = event.get("message")
                                    .and_then(|m| m.as_str())
                                    .unwrap_or("Unknown error");
                                return Err(format!("Streaming error: {}", error_msg));
                            }
                            "FUNCTION_CALL_STARTED" => {
                                if let Some(function_name) = event.get("function_name").and_then(|f| f.as_str()) {
                                    println!("\n🔧 Function call: {}", function_name);
                                }
                            }
                            "FUNCTION_CALL_COMPLETED" => {
                                if let Some(result) = event.get("result") {
                                    println!("✅ Function result: {:?}", result);
                                }
                            }
                            _ => {
                                // Other event types
                                // println!("\n📡 Event: {}", event_type);
                            }
                        }
                    }
                }
            }
            
            Ok((chunks, final_response))
        }
    ).await;
    
    match stream_result {
        Ok(result) => result,
        Err(_) => Err(format!("Streaming timed out after {} seconds", timeout_secs)),
    }
}

/// Test basic streaming functionality with real-time response chunks
#[tokio::test]
async fn test_basic_streaming_response() {
    let api_key = read_api_key_from_config()
        .or_else(|_| std::env::var("OPENROUTER_API_KEY").map_err(|e| Box::new(e) as Box<dyn std::error::Error>))
        .unwrap_or_else(|_: Box<dyn std::error::Error>| {
            println!("Skipping streaming test - no API key found");
            return String::new();
        });
    
    if api_key.is_empty() {
        return;
    }

    let agent = AgentBuilder::new("Streaming Test Agent")
        .with_instructions("You are a helpful assistant. Provide detailed responses to demonstrate streaming.")
        .with_provider(ProviderConfig {
            provider: ChatProvider::OpenRouter,
            model_name: "google/gemini-2.5-pro".to_string(),
            api_key: Some(api_key),
            endpoint: Some("https://openrouter.ai/api/v1".to_string()),
        })
        .with_plugin(MathPlugin { name: "StreamMath".to_string() })
        .with_max_function_calls(3)
        .build()
        .expect("Failed to create streaming agent");

    let conversation = Conversation::new(vec![agent])
        .expect("Failed to create conversation");

    println!("🚀 Starting streaming test with question about mathematics...");
    
    let start_time = Instant::now();
    let (chunks, final_response) = collect_streaming_response(
        &conversation,
        "Explain the concept of calculus and then calculate 25 * 4 using the multiply function",
        30
    ).await.expect("Streaming test failed");

    let total_time = start_time.elapsed();

    println!("\n✅ Streaming completed successfully!");
    println!("📝 Final response length: {} characters", final_response.len());
    
    // Verify we received chunks and final response
    assert!(!chunks.is_empty(), "Should have received streaming chunks");
    assert!(!final_response.is_empty(), "Final response should not be empty");
    
    println!("📊 Streaming Statistics:");
    println!("   Total chunks received: {}", chunks.len());
    println!("   Total streaming time: {:?}", total_time);
    println!("   Final response length: {} characters", final_response.len());
    
    // Verify the response contains mathematical content
    let response_lower = final_response.to_lowercase();
    let has_math_content = response_lower.contains("calculus") || 
                          response_lower.contains("100") || // 25 * 4 = 100
                          response_lower.contains("multiply") ||
                          response_lower.contains("mathematics");
    
    assert!(has_math_content, "Response should contain mathematical content");
}

/// Test streaming with function calls to ensure proper chunking during tool usage
#[tokio::test]
async fn test_streaming_with_function_calls() {
    let api_key = read_api_key_from_config()
        .or_else(|_| std::env::var("OPENROUTER_API_KEY").map_err(|e| Box::new(e) as Box<dyn std::error::Error>))
        .unwrap_or_else(|_: Box<dyn std::error::Error>| {
            println!("Skipping function call streaming test - no API key found");
            return String::new();
        });
    
    if api_key.is_empty() {
        return;
    }

    let agent = AgentBuilder::new("Function Call Streaming Agent")
        .with_instructions("Use the available math functions when asked to perform calculations. Explain your process step by step.")
        .with_provider(ProviderConfig {
            provider: ChatProvider::OpenRouter,
            model_name: "google/gemini-2.5-pro".to_string(),
            api_key: Some(api_key),
            endpoint: Some("https://openrouter.ai/api/v1".to_string()),
        })
        .with_plugin(MathPlugin { name: "FunctionStreamMath".to_string() })
        .with_plugin(StringPlugin { operations_count: 0 })
        .with_max_function_calls(5)
        .build()
        .expect("Failed to create function call streaming agent");

    let conversation = Conversation::new(vec![agent])
        .expect("Failed to create conversation");

    println!("🔧 Testing streaming with function calls...");
    
    let (chunks, final_response) = collect_streaming_response(
        &conversation,
        "Please calculate these step by step: First add 15 + 25, then multiply that result by 3, and finally find the square root of the final result. Use the appropriate math functions.",
        45
    ).await.expect("Function call streaming test failed");

    println!("\n✅ Function call streaming test completed!");
    
    assert!(!chunks.is_empty(), "Should have received chunks during function calls");
    assert!(!final_response.is_empty(), "Final response should contain calculation results");
    
    println!("📊 Function Call Streaming Results:");
    println!("   Chunks received: {}", chunks.len());
    println!("   Final response length: {} chars", final_response.len());
    
    // Verify the response contains evidence of calculations
    let response_lower = final_response.to_lowercase();
    let has_calculations = response_lower.contains("40") || // 15+25
                          response_lower.contains("120") || // 40*3  
                          response_lower.contains("add") ||
                          response_lower.contains("multiply") ||
                          response_lower.contains("square root");
    
    // Check if function call events were present in the chunks
    let function_call_events = chunks.iter().any(|chunk| {
        if let Ok(event) = serde_json::from_str::<Value>(chunk) {
            event.get("type").and_then(|t| t.as_str()) == Some("function_call")
        } else {
            false
        }
    });
    
    println!("   Function call events detected: {}", function_call_events);
    assert!(has_calculations, "Response should contain evidence of mathematical calculations");
}

/// Test streaming performance and responsiveness
#[tokio::test]
async fn test_streaming_performance() {
    let api_key = read_api_key_from_config()
        .or_else(|_| std::env::var("OPENROUTER_API_KEY").map_err(|e| Box::new(e) as Box<dyn std::error::Error>))
        .unwrap_or_else(|_: Box<dyn std::error::Error>| {
            println!("Skipping streaming performance test - no API key found");
            return String::new();
        });
    
    if api_key.is_empty() {
        return;
    }

    let agent = AgentBuilder::new("Performance Test Agent")
        .with_instructions("Provide detailed, comprehensive responses to test streaming performance.")
        .with_provider(ProviderConfig {
            provider: ChatProvider::OpenRouter,
            model_name: "google/gemini-2.5-pro".to_string(),
            api_key: Some(api_key),
            endpoint: Some("https://openrouter.ai/api/v1".to_string()),
        })
        .build()
        .expect("Failed to create performance test agent");

    let conversation = Conversation::new(vec![agent])
        .expect("Failed to create conversation");

    println!("⚡ Testing streaming performance with a complex query...");
    
    let performance_start = Instant::now();
    
    let (chunks, final_response) = collect_streaming_response(
        &conversation,
        "Write a comprehensive explanation of how artificial intelligence works, including machine learning, neural networks, and natural language processing. Make it detailed and educational.",
        60
    ).await.expect("Performance streaming test failed");

    let total_time = performance_start.elapsed();

    let total_chars = final_response.len();
    let chars_per_second = if total_time.as_secs() > 0 { 
        total_chars / total_time.as_secs() as usize 
    } else { 
        total_chars 
    };

    println!("\n⚡ Streaming Performance Results:");
    println!("   Total time: {:?}", total_time);
    println!("   Total chunks: {}", chunks.len());
    println!("   Total characters: {}", total_chars);
    println!("   Characters per second: {}", chars_per_second);
    println!("   Final response length: {} chars", final_response.len());

    // Performance assertions
    assert!(chunks.len() > 5, "Should receive multiple chunks for a long response");
    assert!(total_chars > 500, "Should receive substantial content");
    assert!(total_time < Duration::from_secs(60), "Should complete within reasonable time");
    
    println!("✅ Streaming performance test passed!");
}

/// Test streaming behavior with stateful conversations
#[tokio::test]
async fn test_stateful_streaming() {
    let api_key = read_api_key_from_config()
        .or_else(|_| std::env::var("OPENROUTER_API_KEY").map_err(|e| Box::new(e) as Box<dyn std::error::Error>))
        .unwrap_or_else(|_: Box<dyn std::error::Error>| {
            println!("Skipping stateful streaming test - no API key found");
            return String::new();
        });
    
    if api_key.is_empty() {
        return;
    }

    let agent = AgentBuilder::new("Stateful Streaming Agent")
        .with_instructions("Remember our conversation history and refer to previous topics when relevant.")
        .with_provider(ProviderConfig {
            provider: ChatProvider::OpenRouter,
            model_name: "google/gemini-2.5-pro".to_string(),
            api_key: Some(api_key),
            endpoint: Some("https://openrouter.ai/api/v1".to_string()),
        })
        .with_plugin(MathPlugin { name: "StatefulStreamMath".to_string() })
        .with_max_function_calls(3)
        .build()
        .expect("Failed to create stateful streaming agent");

    let conversation = Conversation::new(vec![agent])
        .expect("Failed to create conversation");

    println!("🔄 Testing stateful streaming across multiple interactions...");

    // First streaming interaction
    println!("1️⃣ First streaming interaction...");
    let (first_chunks, response1) = collect_streaming_response(
        &conversation,
        "My favorite number is 42. Please calculate 42 + 8 using the add function.",
        30
    ).await.expect("First streaming message failed");

    println!("\n--- First Response Complete ---\n");

    // Small delay between messages
    tokio::time::sleep(Duration::from_millis(1000)).await;

    // Second streaming interaction - should reference first
    println!("2️⃣ Second streaming interaction...");
    let (second_chunks, response2) = collect_streaming_response(
        &conversation,
        "What was my favorite number and what did we calculate with it?",
        30
    ).await.expect("Second streaming message failed");

    println!("\n--- Second Response Complete ---\n");

    // Verify both responses
    assert!(!response1.is_empty(), "First streaming response should not be empty");
    assert!(!response2.is_empty(), "Second streaming response should not be empty");

    // Check for memory/context in the second response
    let response2_lower = response2.to_lowercase();
    let remembers_context = response2_lower.contains("42") || 
                           response2_lower.contains("favorite") ||
                           response2_lower.contains("50") || // 42 + 8 = 50
                           response2_lower.contains("previous") ||
                           response2_lower.contains("earlier");

    println!("🧠 Stateful Streaming Results:");
    println!("   First response chunks: {}", first_chunks.len());
    println!("   Second response chunks: {}", second_chunks.len());
    println!("   Remembers context: {}", remembers_context);
    println!("   First response length: {} chars", response1.len());
    println!("   Second response length: {} chars", response2.len());

    assert!(first_chunks.len() > 0, "Should receive chunks in first streaming response");
    assert!(second_chunks.len() > 0, "Should receive chunks in second streaming response");
    assert!(remembers_context, "Agent should remember context from previous streaming interaction");

    println!("✅ Stateful streaming test passed!");
}
