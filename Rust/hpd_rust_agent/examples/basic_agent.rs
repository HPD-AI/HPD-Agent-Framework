//! Basic Agent Example
//! 
//! This example demonstrates how to create a simple AI agent and have a conversation.
//! This is the minimal setup needed to get started with the HPD Rust Agent Library.

use hpd_rust_agent::{AgentBuilder, Conversation, AppSettings};

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    println!("🚀 HPD Rust Agent Library - Basic Agent Example");
    println!("================================================");

    // Step 1: Load configuration from appsettings.json
    println!("📄 Loading configuration...");
    let config = AppSettings::load()
        .map_err(|e| format!("Failed to load configuration: {}", e))?;
    
    // Step 2: Get API credentials
    let api_key = config.get_openrouter_api_key()
        .ok_or("❌ OpenRouter API key not found in configuration. Please add it to appsettings.json")?;
    
    let model = config.get_default_model()
        .unwrap_or("google/gemini-2.5-pro");
    
    println!("✅ Configuration loaded successfully");
    println!("   Model: {}", model);

    // Step 3: Create an AI agent with basic configuration
    println!("\n🤖 Creating AI agent...");
    let agent = AgentBuilder::new("basic-assistant")
        .with_instructions("You are a helpful AI assistant. Be friendly and concise.")
        .with_max_function_calls(5)
        .with_max_conversation_history(10)
        .with_openrouter(model, api_key)
        .build()
        .map_err(|e| format!("Failed to create agent: {}", e))?;
    
    println!("✅ Agent created successfully");

    // Step 4: Create a conversation
    println!("\n💬 Starting conversation...");
    let conversation = Conversation::new(vec![agent])
        .map_err(|e| format!("Failed to create conversation: {}", e))?;
    
    println!("✅ Conversation initialized");

    // Step 5: Send some messages
    let messages = vec![
        "Hello! Can you introduce yourself?",
        "What can you help me with?",
        "Tell me a fun fact about Rust programming.",
    ];

    for (i, message) in messages.iter().enumerate() {
        println!("\n📤 Sending message {}: {}", i + 1, message);
        
        match conversation.send(message) {
            Ok(response) => {
                println!("📥 Response: {}", response);
            }
            Err(e) => {
                eprintln!("❌ Error sending message: {}", e);
            }
        }
        
        // Small delay between messages
        tokio::time::sleep(tokio::time::Duration::from_millis(500)).await;
    }

    println!("\n✨ Basic agent example completed successfully!");
    println!("\n🎯 Key Points:");
    println!("   • Agent configuration is loaded from appsettings.json");
    println!("   • RustAgentBuilder provides a fluent API for configuration");
    println!("   • Conversations manage the agent lifecycle automatically");
    println!("   • Error handling ensures graceful failure modes");
    
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn test_basic_agent_creation() {
        // This test demonstrates the basic agent creation without API calls
        let config = AppSettings::load().expect("Config should load");
        
        if let Some(api_key) = config.get_openrouter_api_key() {
            let agent = AgentBuilder::new("test-agent")
                .with_instructions("Test instructions")
                .with_openrouter("google/gemini-2.5-pro", api_key)
                .build();
            
            assert!(agent.is_ok(), "Agent creation should succeed");
        }
    }

    #[test]
    fn test_agent_builder_chain() {
        // Test the builder pattern without actually building
        let builder = AgentBuilder::new("test")
            .with_instructions("Test")
            .with_max_function_calls(10)
            .with_max_conversation_history(20);
        
        // Verify the builder can be chained
        let json = builder.debug_json();
        assert!(json.contains("test"));
        assert!(json.contains("Test"));
    }
}
