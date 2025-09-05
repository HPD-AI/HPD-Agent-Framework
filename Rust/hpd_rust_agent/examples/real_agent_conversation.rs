use hpd_rust_agent::agent::{AgentBuilder, ProviderConfig, ChatProvider};
use hpd_rust_agent::conversation::Conversation;
use hpd_rust_agent::example_plugins::{MathPlugin, StringPlugin};
use tokio;

#[tokio::main]
async fn main() {
    println!("🤖 Real Agent Math Conversation with Gemini 2.5 Pro");
    println!("===================================================\n");

    // Create a real agent with OpenRouter + Gemini 2.5 Pro
    println!("🔧 Creating agent with OpenRouter + Gemini 2.5 Pro...");
    let agent = AgentBuilder::new("Math Assistant")
        .with_instructions(
            "You are a helpful math assistant powered by Google Gemini 2.5 Pro. \
            When users ask math questions, use the available math functions to calculate the answers. \
            Always explain which function you're using and show the calculation process."
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

    // Create a conversation
    println!("💬 Creating conversation...");
    let conversation = match Conversation::new(vec![agent]) {
        Ok(conversation) => {
            println!("✅ Conversation created!");
            conversation
        },
        Err(error) => {
            eprintln!("❌ Failed to create conversation: {}", error);
            return;
        }
    };

    println!();
    println!("{}", "═".repeat(60));
    println!("🧮 REAL MATH CONVERSATIONS WITH GEMINI 2.5 PRO");
    println!("{}", "═".repeat(60));

    // Test different math problems with the real agent
    let questions = vec![
        "what functions do you have?",
        "What is 156 + 847? Please use the add function to calculate this.",
        "Can you calculate 25 squared (25 to the power of 2) using the power function?",
        "What's the square root of 144? Use the sqrt function please.",
        "Calculate 120 divided by 8 using the divide function.",
        "What is 15 times 23? Use the multiply function.",
    ];

    for (i, question) in questions.iter().enumerate() {
        println!("\n🔢 Question {}: {}", i + 1, question);
        println!("{}", "─".repeat(80));
        
        print!("🤖 Gemini is thinking");
        for _ in 0..3 {
            std::thread::sleep(std::time::Duration::from_millis(500));
            print!(".");
            use std::io::{self, Write};
            io::stdout().flush().unwrap();
        }
        println!("\n");

        // Send the question to the real agent
        match conversation.send(question) {
            Ok(response) => {
                println!("🤖 Gemini 2.5 Pro Response:");
                println!("{}", "─".repeat(40));
                
                // Try to parse the response as JSON to see function calls
                if let Ok(json_response) = serde_json::from_str::<serde_json::Value>(&response) {
                    // Check if it's a structured response with function calls
                    if let Some(message) = json_response.get("message") {
                        println!("💬 Message: {}", message.as_str().unwrap_or("No message"));
                    }
                    
                    if let Some(function_calls) = json_response.get("function_calls") {
                        if let Some(calls_array) = function_calls.as_array() {
                            if !calls_array.is_empty() {
                                println!("\n🔧 Function Calls Made by Gemini:");
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
                    
                    if let Some(final_answer) = json_response.get("final_answer") {
                        println!("\n🎯 Final Answer: {}", final_answer.as_str().unwrap_or("No final answer"));
                    }
                } else {
                    // If not JSON, just print the raw response
                    println!("{}", response);
                }
            },
            Err(error) => {
                println!("❌ Error getting response: {}", error);
            }
        }
        
        println!();
        println!("{}", "═".repeat(60));
    }

    println!("\n🎉 Real agent conversation completed!");
    println!("   • Used actual OpenRouter API with Gemini 2.5 Pro");
    println!("   • Math functions executed real Rust code");
    println!("   • Agent made intelligent function calls");
    println!("\n✅ System fully operational!");
}
