use hpd_rust_agent::agent::{AgentBuilder, ProviderConfig, ChatProvider};
use hpd_rust_agent::conversation::Conversation;
use hpd_rust_agent::example_plugins::{MathPlugin, StringPlugin};
use tokio;

#[tokio::main]
async fn main() {
    println!("⚖️  Stateful vs Stateless Conversation Comparison");
    println!("================================================\n");

    println!("This test demonstrates the key difference between:");
    println!("  🔄 STATEFUL: Single conversation with persistent memory");
    println!("  🆕 STATELESS: New conversation for each interaction");
    println!();

    // Create agent factory function to avoid code duplication
    let create_agent = || async {
        AgentBuilder::new("Memory Test Assistant")
            .with_instructions(
                "You are a helpful assistant. When users refer to previous calculations or context, \
                acknowledge what you remember from the conversation history."
            )
            .with_provider(ProviderConfig {
                provider: ChatProvider::OpenRouter,
                model_name: "google/gemini-2.5-pro".to_string(),
                api_key: Some("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".to_string()),
                endpoint: Some("https://openrouter.ai/api/v1".to_string()),
            })
            .with_plugin(MathPlugin { name: "MathPlugin".to_string() })
            .with_plugin(StringPlugin { operations_count: 0 })
            .with_max_function_calls(3)
            .build()
    };

    // Test questions that require memory/context
    let test_questions = vec![
        "What is 25 + 17? Please use the add function.",
        "What was the result of the previous calculation?",
        "Multiply that result by 2 using the multiply function.",
        "Can you tell me what calculations we've done so far?",
    ];

    println!("{}", "═".repeat(70));
    println!("🔄 STATEFUL CONVERSATION TEST");
    println!("{}", "═".repeat(70));
    
    // Create ONE conversation for all interactions (STATEFUL)
    println!("🔧 Creating single persistent conversation...");
    let stateful_agent = create_agent().await.expect("Failed to create stateful agent");
    let stateful_conversation = Conversation::new(vec![stateful_agent])
        .expect("Failed to create stateful conversation");
    println!("✅ Stateful conversation ready\n");

    for (i, question) in test_questions.iter().enumerate() {
        println!("📝 Question {}: {}", i + 1, question);
        print!("🤖 Thinking");
        for _ in 0..2 {
            std::thread::sleep(std::time::Duration::from_millis(400));
            print!(".");
            use std::io::{self, Write};
            io::stdout().flush().unwrap();
        }
        println!();
        
        match stateful_conversation.send(question) {
            Ok(response) => {
                if let Ok(json_response) = serde_json::from_str::<serde_json::Value>(&response) {
                    if let Some(message) = json_response.get("message") {
                        println!("💬 Response: {}", message.as_str().unwrap_or("No message"));
                    } else if let Some(final_answer) = json_response.get("final_answer") {
                        println!("💬 Response: {}", final_answer.as_str().unwrap_or("No final answer"));
                    }
                } else {
                    println!("💬 Response: {}", response);
                }
            },
            Err(error) => {
                println!("❌ Error: {}", error);
            }
        }
        println!();
    }

    println!("{}", "═".repeat(70));
    println!("🆕 STATELESS CONVERSATION TEST");
    println!("{}", "═".repeat(70));
    
    // Create NEW conversation for each interaction (STATELESS)
    println!("🔧 Creating fresh conversations for each question...\n");

    for (i, question) in test_questions.iter().enumerate() {
        // Create a brand new conversation for each question
        println!("📝 Question {} (NEW conversation): {}", i + 1, question);
        
        let stateless_agent = create_agent().await.expect("Failed to create stateless agent");
        let stateless_conversation = Conversation::new(vec![stateless_agent])
            .expect("Failed to create stateless conversation");
        
        print!("🤖 Thinking (no prior context)");
        for _ in 0..2 {
            std::thread::sleep(std::time::Duration::from_millis(400));
            print!(".");
            use std::io::{self, Write};
            io::stdout().flush().unwrap();
        }
        println!();
        
        match stateless_conversation.send(question) {
            Ok(response) => {
                if let Ok(json_response) = serde_json::from_str::<serde_json::Value>(&response) {
                    if let Some(message) = json_response.get("message") {
                        println!("💬 Response: {}", message.as_str().unwrap_or("No message"));
                    } else if let Some(final_answer) = json_response.get("final_answer") {
                        println!("💬 Response: {}", final_answer.as_str().unwrap_or("No final answer"));
                    }
                } else {
                    println!("💬 Response: {}", response);
                }
            },
            Err(error) => {
                println!("❌ Error: {}", error);
            }
        }
        println!();
        
        // Conversation is automatically dropped here, losing all context
    }

    println!("{}", "═".repeat(70));
    println!("📊 ANALYSIS & EXPECTED RESULTS");
    println!("{}", "═".repeat(70));
    
    println!("🔄 STATEFUL Results Expected:");
    println!("   ✅ Question 1: Should calculate 25 + 17 = 42");
    println!("   ✅ Question 2: Should remember '42' from previous calculation");
    println!("   ✅ Question 3: Should multiply 42 * 2 = 84 using context");
    println!("   ✅ Question 4: Should list all calculations: 25+17=42, then 42*2=84");
    
    println!("\n🆕 STATELESS Results Expected:");
    println!("   ✅ Question 1: Should calculate 25 + 17 = 42");
    println!("   ❌ Question 2: Should say 'I don't have context' or similar");
    println!("   ❌ Question 3: Should ask 'what result?' - no memory of 42");
    println!("   ❌ Question 4: Should say 'no previous calculations' - fresh start");
    
    println!("\n🎯 KEY INSIGHTS:");
    println!("   • Stateful = Same Conversation instance across all send() calls");
    println!("   • Stateless = New Conversation instance for each interaction");
    println!("   • The C# backend maintains message history per conversation");
    println!("   • Context/memory is conversation-scoped, not global");
    
    println!("\n✅ Comparison test completed!");
    println!("This clearly demonstrates why stateful conversations are crucial for:");
    println!("   • Multi-turn dialogues");
    println!("   • Building complex workflows"); 
    println!("   • Maintaining user context");
    println!("   • Creating intelligent assistants");
}
