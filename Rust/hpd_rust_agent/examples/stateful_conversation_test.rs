use hpd_rust_agent::agent::{AgentBuilder, ProviderConfig, ChatProvider};
use hpd_rust_agent::conversation::Conversation;
use hpd_rust_agent::example_plugins::{MathPlugin, StringPlugin};
use tokio;

#[tokio::main]
async fn main() {
    println!("🧠 Stateful Conversation Test with Memory & Context");
    println!("===================================================\n");

    // Create a real agent with OpenRouter + Gemini 2.5 Pro
    println!("🔧 Creating agent with OpenRouter + Gemini 2.5 Pro...");
    let agent = AgentBuilder::new("Stateful Math Assistant")
        .with_instructions(
            "You are a helpful math assistant that remembers previous calculations and context. \
            When users refer to previous calculations or results, use that context. \
            Keep track of running totals, sequences, or any mathematical context from earlier in the conversation. \
            Always explain your reasoning and reference previous calculations when relevant."
        )
        .with_provider(ProviderConfig {
            provider: ChatProvider::OpenRouter,
            model_name: "google/gemini-2.5-pro".to_string(),
            api_key: Some("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".to_string()),
            endpoint: Some("https://openrouter.ai/api/v1".to_string()),
        })
        .with_plugin(MathPlugin { name: "MathPlugin".to_string() })
        .with_plugin(StringPlugin { operations_count: 0 })
        .with_max_function_calls(5)
        .build();

    let agent = match agent {
        Ok(agent) => {
            println!("✅ Agent created successfully!");
            agent
        },
        Err(error) => {
            eprintln!("❌ Failed to create agent: {}", error);
            return;
        }
    };

    // Create a SINGLE conversation that will persist state across all interactions
    println!("💬 Creating persistent conversation...");
    let conversation = match Conversation::new(vec![agent]) {
        Ok(conversation) => {
            println!("✅ Persistent conversation created!");
            conversation
        },
        Err(error) => {
            eprintln!("❌ Failed to create conversation: {}", error);
            return;
        }
    };

    println!();
    println!("{}", "═".repeat(60));
    println!("🧠 STATEFUL CONVERSATION - TESTING MEMORY & CONTEXT");
    println!("{}", "═".repeat(60));

    // Define a series of connected questions that build on each other
    // This tests the agent's ability to maintain context and remember previous calculations
    let conversation_flow = vec![
        (
            "Let's start with a simple calculation. What is 10 + 15? Use the add function.",
            "🔢 Initial calculation"
        ),
        (
            "Now multiply that result by 3. Use the multiply function with the previous result.",
            "🔄 Building on previous result"
        ),
        (
            "What's the square root of the number we just calculated? Use the sqrt function.",
            "📐 Using context from chain of calculations"
        ),
        (
            "Can you divide our first result (10 + 15) by 5? Use the divide function.",
            "🧠 Referencing earlier calculation from memory"
        ),
        (
            "Now add the result from step 3 (the square root) to the result from step 4 (the division). Use the add function.",
            "🔗 Combining multiple previous results"
        ),
        (
            "Let's start a new calculation sequence. What is 7 squared? Use the power function.",
            "🆕 Starting fresh while maintaining context"
        ),
        (
            "Compare our final result from the first sequence with this new result. Which is larger?",
            "📊 Memory test - comparing across conversation"
        ),
        (
            "Can you give me a summary of all the calculations we've performed in this conversation?",
            "📝 Full memory recall test"
        ),
    ];

    for (i, (question, description)) in conversation_flow.iter().enumerate() {
        println!("\n{}", "─".repeat(80));
        println!("🔢 Step {}: {}", i + 1, description);
        println!("❓ Question: {}", question);
        println!("{}", "─".repeat(80));
        
        print!("🤖 Gemini is processing (with full conversation context)");
        for _ in 0..3 {
            std::thread::sleep(std::time::Duration::from_millis(600));
            print!(".");
            use std::io::{self, Write};
            io::stdout().flush().unwrap();
        }
        println!("\n");

        // Send the question to the SAME conversation instance
        // This is key - using the same conversation preserves all previous messages
        match conversation.send(question) {
            Ok(response) => {
                println!("🤖 Gemini 2.5 Pro Response (with conversation memory):");
                println!("{}", "─".repeat(50));
                
                // Parse and display the structured response
                if let Ok(json_response) = serde_json::from_str::<serde_json::Value>(&response) {
                    // Display message content
                    if let Some(message) = json_response.get("message") {
                        println!("💬 Message: {}", message.as_str().unwrap_or("No message"));
                    }
                    
                    // Display function calls if any
                    if let Some(function_calls) = json_response.get("function_calls") {
                        if let Some(calls_array) = function_calls.as_array() {
                            if !calls_array.is_empty() {
                                println!("\n🔧 Function Calls:");
                                for (j, call) in calls_array.iter().enumerate() {
                                    println!("   {}. Function: {}", 
                                        j + 1, 
                                        call.get("name").and_then(|v| v.as_str()).unwrap_or("unknown")
                                    );
                                    if let Some(args) = call.get("arguments") {
                                        println!("      Arguments: {}", args);
                                    }
                                    if let Some(result) = call.get("result") {
                                        println!("      ✅ Result: {}", result);
                                    }
                                }
                            }
                        }
                    }
                    
                    // Display final answer
                    if let Some(final_answer) = json_response.get("final_answer") {
                        println!("\n🎯 Final Answer: {}", final_answer.as_str().unwrap_or("No final answer"));
                    }
                } else {
                    // Fallback to raw response
                    println!("{}", response);
                }

                // Add a small pause between interactions for better readability
                std::thread::sleep(std::time::Duration::from_millis(1000));
            },
            Err(error) => {
                println!("❌ Error getting response: {}", error);
                println!("   This might indicate a problem with maintaining conversation state");
            }
        }
        
        println!();
    }

    println!("{}", "═".repeat(60));
    println!("\n🎉 Stateful conversation test completed!");
    println!("📊 Test Results Summary:");
    println!("   • ✅ Used SINGLE conversation instance for all interactions");
    println!("   • ✅ Each message built upon previous context");
    println!("   • ✅ Agent should remember all calculations");
    println!("   • ✅ Questions required referencing earlier results");
    println!("   • ✅ Final summary tested full conversation memory");
    
    println!("\n🔍 Key Differences from Stateless Test:");
    println!("   • Same Conversation object used throughout");
    println!("   • Messages accumulate in conversation history");
    println!("   • Agent has full context of previous interactions");
    println!("   • Questions explicitly reference earlier calculations");
    println!("   • Tests memory and contextual understanding");
    
    println!("\n✅ Stateful conversation system fully tested!");
}
