//! Plugin Development Example
//! 
//! This example demonstrates the ergonomic plugin system with procedural macros.
//! Learn how to create AI plugins with zero boilerplate using the #[hpd_plugin] macro.

use hpd_rust_agent::{
    hpd_plugin, ai_function, requires_permission,
    AgentBuilder, Conversation, AppSettings,
    get_registered_plugins, get_plugin_stats
};
use std::sync::atomic::{AtomicU32, Ordering};

/// A comprehensive example plugin demonstrating all features
#[derive(Default)]
pub struct ComprehensivePlugin {
    /// Track how many operations this plugin has performed
    operation_count: AtomicU32,
    /// Plugin instance ID for tracking
    instance_id: String,
}

impl ComprehensivePlugin {
    /// Create a new plugin instance with a unique ID
    pub fn new(instance_id: &str) -> Self {
        Self {
            operation_count: AtomicU32::new(0),
            instance_id: instance_id.to_string(),
        }
    }
}

/// The #[hpd_plugin] macro makes this struct a full-featured AI plugin
/// - Automatically implements the Plugin trait
/// - Generates FFI wrapper functions for C# integration
/// - Creates JSON schemas for OpenAI function calling
/// - Registers the plugin at startup with #[ctor]
#[hpd_plugin("Comprehensive", "A comprehensive example plugin demonstrating all features")]
impl ComprehensivePlugin {
    /// Simple function with basic parameters
    #[ai_function("Greets the user with a personalized message")]
    pub async fn greet_user(&self, name: String, language: String) -> String {
        self.operation_count.fetch_add(1, Ordering::SeqCst);
        
        let greeting = match language.to_lowercase().as_str() {
            "spanish" => format!("¡Hola, {}!", name),
            "french" => format!("Bonjour, {}!", name),
            "german" => format!("Hallo, {}!", name),
            "japanese" => format!("こんにちは、{}さん!", name),
            _ => format!("Hello, {}!", name),
        };
        
        serde_json::json!({
            "greeting": greeting,
            "language": language,
            "instance_id": self.instance_id,
            "success": true
        }).to_string()
    }

    /// Function with numeric parameters and calculations
    #[ai_function("Calculates compound interest over time")]
    pub async fn calculate_compound_interest(
        &self, 
        principal: f64, 
        rate: f64, 
        time: f64, 
        compounds_per_year: i32
    ) -> String {
        self.operation_count.fetch_add(1, Ordering::SeqCst);
        
        if principal <= 0.0 || rate < 0.0 || time <= 0.0 || compounds_per_year <= 0 {
            return serde_json::json!({
                "error": "Invalid parameters: principal > 0, rate >= 0, time > 0, compounds > 0",
                "success": false
            }).to_string();
        }
        
        let amount = principal * (1.0 + rate / 100.0 / compounds_per_year as f64)
            .powf(compounds_per_year as f64 * time);
        let interest = amount - principal;
        
        serde_json::json!({
            "principal": principal,
            "rate_percent": rate,
            "time_years": time,
            "compounds_per_year": compounds_per_year,
            "final_amount": amount,
            "total_interest": interest,
            "success": true
        }).to_string()
    }

    /// Function with custom name (different from method name)
    #[ai_function("Gets current system information", name = "get_system_info")]
    pub async fn system_information(&self) -> String {
        self.operation_count.fetch_add(1, Ordering::SeqCst);
        
        let os = std::env::consts::OS;
        let arch = std::env::consts::ARCH;
        let family = std::env::consts::FAMILY;
        
        serde_json::json!({
            "operating_system": os,
            "architecture": arch,
            "family": family,
            "plugin_instance": self.instance_id,
            "rust_version": "1.70+",
            "success": true
        }).to_string()
    }

    /// Function requiring user permission (dangerous operation)
    #[ai_function("Simulates a file deletion operation (safe simulation only)")]
    #[requires_permission]
    pub async fn simulate_file_deletion(&self, file_path: String) -> String {
        self.operation_count.fetch_add(1, Ordering::SeqCst);
        
        // This is just a simulation - no actual file operations
        // The #[requires_permission] attribute will trigger the permission system
        
        if file_path.is_empty() {
            return serde_json::json!({
                "error": "File path cannot be empty",
                "success": false
            }).to_string();
        }
        
        // Simulate different outcomes based on file extension
        let result = if file_path.ends_with(".important") {
            serde_json::json!({
                "action": "deletion_blocked",
                "reason": "File marked as important",
                "file_path": file_path,
                "success": false
            })
        } else {
            serde_json::json!({
                "action": "deletion_simulated",
                "message": "File would be deleted in real operation",
                "file_path": file_path,
                "timestamp": "2024-09-01T00:00:00Z",
                "success": true
            })
        };
        
        result.to_string()
    }

    /// Async function with complex data structures
    #[ai_function("Generates a weather report for multiple cities")]
    pub async fn generate_weather_report(&self, cities: String) -> String {
        self.operation_count.fetch_add(1, Ordering::SeqCst);
        
        // Parse comma-separated cities
        let city_list: Vec<&str> = cities.split(',')
            .map(|s| s.trim())
            .filter(|s| !s.is_empty())
            .collect();
        
        if city_list.is_empty() {
            return serde_json::json!({
                "error": "No cities provided. Use comma-separated format: 'New York, London, Tokyo'",
                "success": false
            }).to_string();
        }
        
        // Simulate weather data generation
        let mut weather_data = Vec::new();
        for city in city_list {
            let temp = 15.0 + (city.len() as f64 * 3.7) % 25.0;
            let conditions = match city.len() % 4 {
                0 => "Sunny",
                1 => "Cloudy", 
                2 => "Rainy",
                _ => "Partly Cloudy",
            };
            
            weather_data.push(serde_json::json!({
                "city": city,
                "temperature_celsius": temp,
                "conditions": conditions,
                "humidity": 45 + (city.len() % 40),
                "generated_at": "2024-09-01T00:00:00Z"
            }));
        }
        
        serde_json::json!({
            "weather_report": weather_data,
            "report_id": format!("WR-{}-{}", self.instance_id, 1672531200),
            "total_cities": weather_data.len(),
            "success": true
        }).to_string()
    }

    /// Plugin introspection function
    #[ai_function("Returns statistics and information about this plugin instance")]
    pub async fn get_plugin_statistics(&self) -> String {
        let count = self.operation_count.load(Ordering::SeqCst);
        
        serde_json::json!({
            "plugin_name": "Comprehensive",
            "instance_id": self.instance_id,
            "operations_performed": count,
            "available_functions": [
                "greet_user",
                "calculate_compound_interest", 
                "get_system_info",
                "simulate_file_deletion",
                "generate_weather_report",
                "get_plugin_statistics"
            ],
            "features": {
                "multi_language_greetings": true,
                "financial_calculations": true,
                "system_information": true,
                "permission_required_operations": true,
                "async_operations": true,
                "complex_data_structures": true
            },
            "created_at": "2024-09-01T00:00:00Z",
            "success": true
        }).to_string()
    }
}

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    println!("🔧 HPD Rust Agent Library - Plugin Development Example");
    println!("======================================================");

    // Step 1: Show plugin registration
    println!("\n📋 Checking registered plugins...");
    let registered = get_registered_plugins();
    println!("✅ Found {} registered plugins:", registered.len());
    for plugin in &registered {
        println!("   • {}", plugin);
    }

    let stats = get_plugin_stats();
    println!("\n📊 Plugin statistics:");
    for stat in &stats {
        println!("   {}", stat);
    }

    // Step 2: Load configuration
    println!("\n📄 Loading configuration...");
    let config = AppSettings::load()
        .map_err(|e| format!("Failed to load configuration: {}", e))?;
    
    let api_key = config.get_openrouter_api_key()
        .ok_or("❌ OpenRouter API key not found in configuration")?;
    
    let model = config.get_default_model()
        .unwrap_or("google/gemini-2.5-pro");
    
    println!("✅ Configuration loaded successfully");

    // Step 3: Create plugin instance
    println!("\n🔧 Creating comprehensive plugin instance...");
    let plugin = ComprehensivePlugin::new("example-2024");

    // Step 4: Create agent with plugin
    println!("\n🤖 Creating AI agent with plugin...");
    let agent = AgentBuilder::new("plugin-demo-agent")
        .with_instructions(
            "You are a helpful assistant with access to a comprehensive plugin. \
             Use the plugin functions to demonstrate their capabilities. \
             Always explain what each function does before calling it."
        )
        .with_max_function_calls(10)
        .with_openrouter(model, api_key)
        .with_plugin(plugin)                    // Add our specific plugin instance
        .with_registered_plugins()              // Add all auto-registered plugins
        .build()
        .map_err(|e| format!("Failed to create agent: {}", e))?;
    
    println!("✅ Agent created with plugins");

    // Step 5: Create conversation
    let conversation = Conversation::new(vec![agent])
        .map_err(|e| format!("Failed to create conversation: {}", e))?;

    // Step 6: Demonstrate plugin functions
    let demo_requests = vec![
        "Greet me in Spanish! My name is Alice.",
        "Calculate compound interest: $1000 principal, 5% annual rate, 10 years, compounded quarterly.",
        "Get information about the current system.",
        "Generate a weather report for New York, London, Tokyo.",
        "Show me statistics about the comprehensive plugin.",
        // Note: The permission-required function would trigger a permission dialog in a real UI
        "Simulate deleting a file called 'test.txt' (this is safe - just a simulation).",
    ];

    for (i, request) in demo_requests.iter().enumerate() {
        println!("\n🎯 Demo {} - Sending: {}", i + 1, request);
        println!("{}", "─".repeat(60));
        
        match conversation.send(request) {
            Ok(response) => {
                println!("📥 Response: {}", response);
            }
            Err(e) => {
                eprintln!("❌ Error: {}", e);
            }
        }
        
        // Small delay between requests
        tokio::time::sleep(tokio::time::Duration::from_millis(1000)).await;
    }

    println!("\n✨ Plugin development example completed!");
    println!("\n🎯 Key Features Demonstrated:");
    println!("   • Zero-boilerplate plugin creation with #[hpd_plugin]");
    println!("   • AI function definitions with #[ai_function]");
    println!("   • Permission system with #[requires_permission]");
    println!("   • Custom function names with name parameter");
    println!("   • Automatic JSON schema generation");
    println!("   • Plugin instance management and statistics");
    println!("   • Complex parameter types and return values");
    println!("   • Auto-registration with constructor injection");

    Ok(())
}

// Add chrono dependency for timestamps
// In a real project, add to Cargo.toml:
// chrono = { version = "0.4", features = ["serde"] }

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn test_plugin_functions() {
        let plugin = ComprehensivePlugin::new("test-instance");

        // Test greeting function
        let result = plugin.greet_user("Alice".to_string(), "spanish".to_string()).await;
        assert!(result.contains("¡Hola, Alice!"));
        assert!(result.contains("success"));

        // Test calculation function
        let result = plugin.calculate_compound_interest(1000.0, 5.0, 10.0, 4).await;
        assert!(result.contains("success"));
        assert!(result.contains("final_amount"));

        // Test system info
        let result = plugin.system_information().await;
        assert!(result.contains("operating_system"));
        assert!(result.contains("test-instance"));

        // Test statistics
        let result = plugin.get_plugin_statistics().await;
        assert!(result.contains("operations_performed"));
        assert!(result.contains("Comprehensive"));
    }

    #[test]
    fn test_plugin_registration() {
        let registered = get_registered_plugins();
        // Should include our ComprehensivePlugin and others
        assert!(!registered.is_empty());
    }
}
