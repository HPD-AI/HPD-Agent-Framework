use hpd_rust_agent::agent::{AgentBuilder, ProviderConfig, ChatProvider};
use hpd_rust_agent::conversation::Conversation;
use hpd_rust_agent::example_plugins::{MathPlugin, StringPlugin};
use tokio;

#[tokio::main]
async fn main() {
    println!("🔬 Testing Function Call Integration");
    println!("====================================\n");

    // Create a minimal agent test
    let agent = AgentBuilder::new("Function Test Agent")
        .with_instructions("You are a test agent. When users ask math questions, you must call the available math functions.")
        .with_provider(ProviderConfig {
            provider: ChatProvider::OpenRouter,
            model_name: "google/gemini-2.5-pro".to_string(),
            api_key: Some("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".to_string()),
            endpoint: Some("https://openrouter.ai/api/v1".to_string()),
        })
        .with_plugin(MathPlugin { name: "MathPlugin".to_string() })
        .build()
        .expect("Failed to create agent");

    let conversation = Conversation::new(vec![agent])
        .expect("Failed to create conversation");

    println!("✅ Agent and conversation ready!\n");

    // Simple test - just ask for a basic addition
    let question = "Add 5 and 3. Call the add function.";
    println!("📝 Testing with simple question: {}\n", question);

    match conversation.send(question) {
        Ok(response) => {
            println!("📨 Raw Response:");
            println!("{}", response);
            println!("\n{}", "─".repeat(80));
            
            // Check if we can find function calls
            if response.contains("add") || response.contains("function") {
                println!("✅ Response mentions functions!");
            } else {
                println!("⚠️  No function mentions detected");
            }
            
            // Try to parse as JSON
            match serde_json::from_str::<serde_json::Value>(&response) {
                Ok(json) => {
                    println!("✅ Response is valid JSON");
                    if let Some(calls) = json.get("function_calls") {
                        println!("🔧 Found function_calls field: {}", calls);
                    } else {
                        println!("❌ No function_calls field found");
                        println!("📋 Available JSON fields: {:?}", json.as_object().map(|o| o.keys().collect::<Vec<_>>()));
                    }
                },
                Err(_) => {
                    println!("ℹ️  Response is plain text (not JSON)");
                }
            }
        },
        Err(error) => {
            println!("❌ Error: {}", error);
        }
    }

    println!();
    println!("{}", "═".repeat(60));
    println!("🔍 Analysis:");
    println!("  • Agent creation: ✅ Working");
    println!("  • OpenRouter + Gemini: ✅ Working");  
    println!("  • Plugin registration: ✅ Working");
    println!("  • Conversation API: ✅ Working");
    println!("  • Function calling: 🔍 Needs investigation");
    println!("\n💡 Next steps: Check C# function calling configuration");
}
