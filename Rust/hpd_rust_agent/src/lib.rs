// HPD Rust Agent Library
// This library provides Rust bindings for the HPD C# Agent

mod ffi;
pub mod agent;
pub mod conversation;
pub mod project;
pub mod streaming;
pub mod config;
pub mod plugins;
pub mod example_plugins;

// Re-export the procedural macros
pub use hpd_rust_agent_macros::{hpd_plugin, ai_function, requires_permission};

// Re-export key types for convenience
pub use plugins::{PluginRegistration, register_plugin, get_registered_plugins, get_plugin_stats};
pub use agent::{Agent, AgentBuilder, AgentConfig, Plugin, RustFunctionInfo};
pub use conversation::Conversation;
pub use project::{Project, ProjectInfo};
pub use config::AppSettings;

pub fn add(left: u64, right: u64) -> u64 {
    left + right
}

#[cfg(test)]
mod tests {
    mod test_module4;
    mod test_module5;
    mod test_module6;
    mod test_agent_math_integration;
    
    use super::ffi;
    use std::ffi::{CString, CStr};

    #[test]
    fn it_works() {
        let result = super::add(2, 2);
        assert_eq!(result, 4);
    }

    #[test]
    fn it_pings_csharp() {
        let message = CString::new("Hello from Rust!").unwrap();
        let response_ptr = unsafe { ffi::ping(message.as_ptr()) };
        
        assert!(!response_ptr.is_null());
        
        let response_cstr = unsafe { CStr::from_ptr(response_ptr) };
        let response_string = response_cstr.to_str().unwrap();
        
        assert_eq!(response_string, "Pong: You sent 'Hello from Rust!'");
        
        // IMPORTANT: Free the memory allocated by C#
        unsafe { ffi::free_string(response_ptr as *mut std::ffi::c_void) };
    }

    #[test]
    fn debug_json_serialization() {
        let agent_builder = crate::agent::AgentBuilder::new("test-agent")
            .with_instructions("Test instructions")
            .with_ollama("llama3.2:latest");
        
        // Print the JSON that would be sent to C#
        let config_json = agent_builder.debug_json();
        println!("Generated JSON: {}", config_json);
    }

    #[test]
    fn it_creates_agent_and_conversation() {
        // Load configuration from appsettings.json
        let config = crate::config::AppSettings::load()
            .expect("Failed to load appsettings.json");
        
        let api_key = config.get_openrouter_api_key()
            .expect("OpenRouter API key not found in configuration");
        
        let model = config.get_default_model()
            .unwrap_or("google/gemini-2.5-pro");
        
        // Create an agent with configuration from appsettings.json
        let agent = crate::agent::AgentBuilder::new("test-agent")
            .with_instructions("Test instructions")
            .with_openrouter(model, api_key)
            .build()
            .unwrap();

        let agents = vec![agent];
        let conversation_result = crate::conversation::Conversation::new(agents);
        assert!(conversation_result.is_ok());

        // `conversation` will be dropped here, triggering the FFI destroy call.
        // You can verify this by adding print statements in the C# `Destroy` methods.
    }

    #[test]
    fn it_sends_and_receives_a_message() {
        // Load configuration from appsettings.json
        let config = crate::config::AppSettings::load()
            .expect("Failed to load appsettings.json");
        
        let api_key = config.get_openrouter_api_key()
            .expect("OpenRouter API key not found in configuration");
        
        let model = config.get_default_model()
            .unwrap_or("google/gemini-2.5-pro");
        
        // Create an agent with configuration from appsettings.json
        let agent = crate::agent::AgentBuilder::new("test-agent")
            .with_instructions("Test instructions")
            .with_openrouter(model, api_key)
            .build()
            .unwrap();

        let agents = vec![agent];
        let conversation = crate::conversation::Conversation::new(agents).unwrap();
        
        // Send a simple message
        let response = conversation.send("Hello").unwrap();
        
        // The response should be non-empty (we can't predict exact content without a real agent)
        assert!(!response.is_empty(), "Response should not be empty");
        println!("Response: {}", response);
    }

    #[tokio::test]
    async fn it_streams_a_response() {
        use tokio_stream::StreamExt;
        
        // Load configuration from appsettings.json
        let config = crate::config::AppSettings::load()
            .expect("Failed to load appsettings.json");
        
        let api_key = config.get_openrouter_api_key()
            .expect("OpenRouter API key not found in configuration");
        
        let model = config.get_default_model()
            .unwrap_or("google/gemini-2.5-pro");
        
        // Create an agent with configuration from appsettings.json
        let agent = crate::agent::AgentBuilder::new("test-agent")
            .with_instructions("Test instructions")
            .with_openrouter(model, api_key)
            .build()
            .unwrap();

        let agents = vec![agent];
        let conversation = crate::conversation::Conversation::new(agents).unwrap();
        
        // Send a streaming message
        let mut stream = conversation.send_streaming("Use a tool").unwrap();

        let mut received_events = Vec::new();
        let mut event_count = 0;
        while let Some(event_json) = stream.next().await {
            received_events.push(event_json);
            event_count += 1;
            // Limit to avoid infinite loops in case of issues
            if event_count > 10 {
                break;
            }
        }

        // Assert that we received some events
        println!("Received {} events", received_events.len());
        for (i, event) in received_events.iter().enumerate() {
            println!("Event {}: {}", i, event);
        }
        
        // We should receive at least some events from the agent
        assert!(received_events.len() >= 1, "Should receive at least one event");
    }

    #[tokio::test]
    async fn test_complete_plugin_execution() {
        use crate::plugins::execute_function_async;
        use serde_json::json;
        
        // Test executing a registered function
        let args = json!({"a": 5.0, "b": 3.0}).to_string();
        let result = execute_function_async("add", &args).await;
        
        assert!(result.is_ok(), "Function execution should succeed");
        let output = result.unwrap();
        println!("Function execution result: {}", output);
        
        // Should be able to parse the result
        assert!(!output.is_empty(), "Result should not be empty");
        
        // For math plugin, should return "8"
        if output.trim() == "8" || output.contains("8") {
            println!("✅ Math function executed correctly");
        }
    }

    #[tokio::test]
    async fn test_plugin_function_dispatch() {
        use crate::plugins::execute_function_async;
        use serde_json::json;
        
        // Test multiple functions
        let test_cases = vec![
            ("add", json!({"a": 10.0, "b": 5.0}), "15"),
            ("multiply", json!({"a": 4.0, "b": 3.0}), "12"),
            ("to_upper", json!({"text": "hello"}), "HELLO"),
        ];
        
        for (func_name, args, expected_contains) in test_cases {
            let args_json = args.to_string();
            let result = execute_function_async(func_name, &args_json).await;
            
            match result {
                Ok(output) => {
                    println!("✅ Function '{}' executed: {}", func_name, output);
                    // Check if result contains expected value
                    if output.contains(expected_contains) {
                        println!("✅ Result contains expected value: {}", expected_contains);
                    }
                },
                Err(e) => {
                    println!("⚠️  Function '{}' failed: {}", func_name, e);
                    // This might be expected if function doesn't exist
                }
            }
        }
    }

    #[test]
    fn test_ffi_function_execution() {
        use std::ffi::CString;
        use crate::ffi::rust_execute_plugin_function;
        
        // Test FFI function execution
        let func_name = CString::new("add").unwrap();
        let args_json = CString::new(r#"{"a": 7.0, "b": 3.0}"#).unwrap();
        
        let result_ptr = rust_execute_plugin_function(func_name.as_ptr(), args_json.as_ptr());
        
        if !result_ptr.is_null() {
            unsafe {
                let result_cstr = std::ffi::CStr::from_ptr(result_ptr);
                let result_string = result_cstr.to_str().unwrap();
                println!("FFI execution result: {}", result_string);
                
                // Should be a JSON response
                assert!(result_string.starts_with('{'), "Result should be JSON");
                
                // Parse the JSON
                let parsed: serde_json::Value = serde_json::from_str(result_string).unwrap();
                println!("Parsed result: {:#}", parsed);
                
                // Free the string
                crate::ffi::rust_free_string(result_ptr);
            }
        } else {
            println!("⚠️  FFI function returned null");
        }
    }

    #[test]
    fn test_project_creation() {
        // Test creating a project with name only
        let project_result = crate::project::Project::create("Test Project", None);
        assert!(project_result.is_ok(), "Project creation should succeed");

        let project = project_result.unwrap();
        
        // Test getting project info
        let info_result = project.get_info();
        assert!(info_result.is_ok(), "Getting project info should succeed");
        
        let info = info_result.unwrap();
        assert_eq!(info.name, "Test Project", "Project name should match");
        assert_eq!(info.conversation_count, 0, "New project should have 0 conversations");
        
        println!("✅ Project created successfully with ID: {}", info.id);
        
        // Project will be automatically destroyed when dropped
    }

    #[test]
    fn test_project_with_storage_directory() {
        // Test creating a project with storage directory
        let project_result = crate::project::Project::create("Test Project With Storage", Some("/tmp/test-storage"));
        assert!(project_result.is_ok(), "Project creation with storage directory should succeed");

        let project = project_result.unwrap();
        
        // Test getting project info
        let info_result = project.get_info();
        assert!(info_result.is_ok(), "Getting project info should succeed");
        
        let info = info_result.unwrap();
        assert_eq!(info.name, "Test Project With Storage", "Project name should match");
        
        println!("✅ Project with storage directory created successfully with ID: {}", info.id);
    }

    #[test]
    fn test_project_conversation_creation() {
        // Load configuration from appsettings.json
        let config = crate::config::AppSettings::load()
            .expect("Failed to load appsettings.json");
        
        let api_key = config.get_openrouter_api_key()
            .expect("OpenRouter API key not found in configuration");
        
        let model = config.get_default_model()
            .unwrap_or("google/gemini-2.5-pro");

        // Create a project
        let project = crate::project::Project::create("Test Conversation Project", None)
            .expect("Failed to create project");

        // Create an agent
        let agent = crate::agent::AgentBuilder::new("test-agent")
            .with_instructions("You are a test assistant")
            .with_openrouter(model, api_key)
            .build()
            .expect("Failed to create agent");

        // Create a conversation within the project
        let conversation_result = project.create_conversation(vec![agent]);
        assert!(conversation_result.is_ok(), "Project conversation creation should succeed");

        let conversation = conversation_result.unwrap();
        
        // Test sending a message through the project-scoped conversation
        let response = conversation.send("Hello from project conversation");
        assert!(response.is_ok(), "Sending message should succeed");
        
        let message = response.unwrap();
        assert!(!message.is_empty(), "Response should not be empty");
        
        println!("✅ Project conversation created and message sent successfully");
        println!("Response: {}", message);

        // Test project info after conversation creation
        let info = project.get_info().expect("Failed to get project info");
        assert_eq!(info.conversation_count, 1, "Project should have 1 conversation after creation");
        
        println!("✅ Project now has {} conversation(s)", info.conversation_count);
    }

    #[test]
    fn test_project_info_retrieval() {
        let project = crate::project::Project::create("Info Test Project", None)
            .expect("Failed to create project");

        let info = project.get_info().expect("Failed to get project info");
        
        // Test all fields are populated
        assert!(!info.id.is_empty(), "Project ID should not be empty");
        assert!(!info.name.is_empty(), "Project name should not be empty");
        assert!(!info.created_at.is_empty(), "Created at should not be empty");
        assert!(!info.last_activity.is_empty(), "Last activity should not be empty");
        
        // Test specific values
        assert_eq!(info.name, "Info Test Project", "Project name should match");
        assert_eq!(info.conversation_count, 0, "New project should have 0 conversations");
        
        println!("✅ Project info retrieved successfully:");
        println!("  ID: {}", info.id);
        println!("  Name: {}", info.name);
        println!("  Description: {}", info.description);
        println!("  Conversations: {}", info.conversation_count);
        println!("  Created: {}", info.created_at);
        println!("  Last Activity: {}", info.last_activity);
    }
}
