use libc::{c_char, c_void, c_int};
use std::ffi::{CStr, CString};
use std::ptr;
use crate::plugins::{get_registered_plugins, get_all_schemas, get_plugin_stats, list_functions};

// Platform-specific library linking
#[cfg(target_os = "windows")]
#[link(name = "HPD-Agent", kind = "dylib")]
extern "C" {
    pub fn ping(message: *const c_char) -> *mut c_char;
    pub fn free_string(ptr: *mut c_void);
    pub fn create_agent_with_plugins(config_json: *const c_char, plugins_json: *const c_char) -> *mut c_void;
    pub fn destroy_agent(agent_handle: *mut c_void);
    pub fn create_conversation(agent_handles: *const *mut c_void, agent_count: c_int) -> *mut c_void;
    pub fn destroy_conversation(conversation_handle: *mut c_void);
    pub fn conversation_send(conversation_handle: *mut c_void, message: *const c_char) -> *mut c_char;
    pub fn conversation_send_streaming(conversation_handle: *mut c_void, message: *const c_char, callback: *const c_void, context: *mut c_void);
    pub fn conversation_send_simple(conversation_handle: *mut c_void, message: *const c_char, callback: *const c_void, context: *mut c_void);
    pub fn create_project(name: *const c_char, storage_directory: *const c_char) -> *mut c_void;
    pub fn project_create_conversation(project_handle: *mut c_void, agent_handles: *const *mut c_void, agent_count: c_int) -> *mut c_void;
    pub fn destroy_project(project_handle: *mut c_void);
    pub fn get_project_info(project_handle: *mut c_void) -> *mut c_char;
}

#[cfg(target_os = "macos")]
#[link(name = "hpdagent", kind = "dylib")]
extern "C" {
    pub fn ping(message: *const c_char) -> *mut c_char;
    pub fn free_string(ptr: *mut c_void);
    pub fn create_agent_with_plugins(config_json: *const c_char, plugins_json: *const c_char) -> *mut c_void;
    pub fn destroy_agent(agent_handle: *mut c_void);
    pub fn create_conversation(agent_handles: *const *mut c_void, agent_count: c_int) -> *mut c_void;
    pub fn destroy_conversation(conversation_handle: *mut c_void);
    pub fn conversation_send(conversation_handle: *mut c_void, message: *const c_char) -> *mut c_char;
    pub fn conversation_send_streaming(conversation_handle: *mut c_void, message: *const c_char, callback: *const c_void, context: *mut c_void);
    pub fn conversation_send_simple(conversation_handle: *mut c_void, message: *const c_char, callback: *const c_void, context: *mut c_void);
    pub fn create_project(name: *const c_char, storage_directory: *const c_char) -> *mut c_void;
    pub fn project_create_conversation(project_handle: *mut c_void, agent_handles: *const *mut c_void, agent_count: c_int) -> *mut c_void;
    pub fn destroy_project(project_handle: *mut c_void);
    pub fn get_project_info(project_handle: *mut c_void) -> *mut c_char;
}

#[cfg(target_os = "linux")]
#[link(name = "HPD-Agent", kind = "dylib")]
extern "C" {
    pub fn ping(message: *const c_char) -> *mut c_char;
    pub fn free_string(ptr: *mut c_void);
    pub fn create_agent_with_plugins(config_json: *const c_char, plugins_json: *const c_char) -> *mut c_void;
    pub fn destroy_agent(agent_handle: *mut c_void);
    pub fn create_conversation(agent_handles: *const *mut c_void, agent_count: c_int) -> *mut c_void;
    pub fn destroy_conversation(conversation_handle: *mut c_void);
    pub fn conversation_send(conversation_handle: *mut c_void, message: *const c_char) -> *mut c_char;
    pub fn conversation_send_streaming(conversation_handle: *mut c_void, message: *const c_char, callback: *const c_void, context: *mut c_void);
    pub fn conversation_send_simple(conversation_handle: *mut c_void, message: *const c_char, callback: *const c_void, context: *mut c_void);
    pub fn create_project(name: *const c_char, storage_directory: *const c_char) -> *mut c_void;
    pub fn project_create_conversation(project_handle: *mut c_void, agent_handles: *const *mut c_void, agent_count: c_int) -> *mut c_void;
    pub fn destroy_project(project_handle: *mut c_void);
    pub fn get_project_info(project_handle: *mut c_void) -> *mut c_char;
}

// Plugin System FFI Functions
// These are exported for C# to call

/// Get all registered plugin information as JSON
#[no_mangle]
pub extern "C" fn rust_get_plugin_registry() -> *mut c_char {
    let plugins = get_registered_plugins();
    let plugin_data = serde_json::json!({
        "plugins": plugins.iter().map(|p| serde_json::json!({
            "name": p.name,
            "description": p.description,
            "functions": p.functions.iter().map(|(name, wrapper)| serde_json::json!({
                "name": name,
                "wrapper": wrapper
            })).collect::<Vec<_>>()
        })).collect::<Vec<_>>()
    });
    
    match serde_json::to_string(&plugin_data) {
        Ok(json_str) => {
            match CString::new(json_str) {
                Ok(c_string) => c_string.into_raw(),
                Err(_) => ptr::null_mut(),
            }
        },
        Err(_) => ptr::null_mut(),
    }
}

/// Get all function schemas as JSON
#[no_mangle]
pub extern "C" fn rust_get_plugin_schemas() -> *mut c_char {
    let schemas = get_all_schemas();
    
    match serde_json::to_string(&schemas) {
        Ok(json_str) => {
            match CString::new(json_str) {
                Ok(c_string) => c_string.into_raw(),
                Err(_) => ptr::null_mut(),
            }
        },
        Err(_) => ptr::null_mut(),
    }
}

/// Get plugin statistics as JSON
#[no_mangle]
pub extern "C" fn rust_get_plugin_stats() -> *mut c_char {
    let stats = get_plugin_stats();
    
    match serde_json::to_string(&stats) {
        Ok(json_str) => {
            match CString::new(json_str) {
                Ok(c_string) => c_string.into_raw(),
                Err(_) => ptr::null_mut(),
            }
        },
        Err(_) => ptr::null_mut(),
    }
}

/// Get list of all available function names as JSON array
#[no_mangle]
pub extern "C" fn rust_get_function_list() -> *mut c_char {
    let functions = list_functions();
    
    match serde_json::to_string(&functions) {
        Ok(json_str) => {
            match CString::new(json_str) {
                Ok(c_string) => c_string.into_raw(),
                Err(_) => ptr::null_mut(),
            }
        },
        Err(_) => ptr::null_mut(),
    }
}

/// Execute a plugin function by name with JSON arguments
#[no_mangle]
pub extern "C" fn rust_execute_plugin_function(
    function_name: *const c_char,
    args_json: *const c_char
) -> *mut c_char {
    if function_name.is_null() || args_json.is_null() {
        return ptr::null_mut();
    }
    
    let result = std::panic::catch_unwind(|| {
        unsafe {
            let func_name = match CStr::from_ptr(function_name).to_str() {
                Ok(s) => s,
                Err(_) => return create_error_response("Invalid function name encoding"),
            };
            
            let args_str = match CStr::from_ptr(args_json).to_str() {
                Ok(s) => s,
                Err(_) => return create_error_response("Invalid arguments JSON encoding"),
            };
            
            // Create a new Tokio runtime for this FFI call since C# calls are sync
            let rt = match tokio::runtime::Runtime::new() {
                Ok(runtime) => runtime,
                Err(_) => return create_error_response("Failed to create async runtime"),
            };
            
            let execution_result = rt.block_on(async {
                crate::plugins::execute_function_async(func_name, args_str).await
            });
            
            match execution_result {
                Ok(output) => {
                    // Parse the output to see if it's already JSON
                    let response = if output.trim().starts_with('{') || output.trim().starts_with('[') {
                        // Already JSON, wrap in success envelope
                        serde_json::json!({"success": true, "result": serde_json::from_str::<serde_json::Value>(&output).unwrap_or(serde_json::Value::String(output))})
                    } else {
                        // Plain value, wrap appropriately
                        serde_json::json!({"success": true, "result": output})
                    };
                    response.to_string()
                },
                Err(error) => serde_json::json!({"success": false, "error": error}).to_string()
            }
        }
    });
    
    match result {
        Ok(json_str) => {
            match CString::new(json_str) {
                Ok(c_string) => c_string.into_raw(),
                Err(_) => ptr::null_mut(),
            }
        },
        Err(_) => {
            let panic_response = create_error_response("Rust panic occurred during function execution");
            match CString::new(panic_response) {
                Ok(c_string) => c_string.into_raw(),
                Err(_) => ptr::null_mut(),
            }
        }
    }
}

/// Helper function to create standardized error responses
fn create_error_response(message: &str) -> String {
    serde_json::json!({
        "success": false,
        "error": message
    }).to_string()
}

/// Free a string allocated by Rust
#[no_mangle]
pub extern "C" fn rust_free_string(ptr: *mut c_char) {
    if !ptr.is_null() {
        unsafe {
            let _ = CString::from_raw(ptr);
        }
    }
}
