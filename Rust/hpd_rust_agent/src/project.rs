//! # Project Module
//! 
//! This module provides the Project API for creating project-scoped conversations and managing
//! shared context across multiple conversations within the same project.
//! 
//! ## Basic Usage
//! 
//! ```rust,no_run
//! use hpd_rust_agent::{Project, AgentBuilder};
//! 
//! // Create a project
//! let project = Project::create("My AI Project", Some("./project-storage"))?;
//! 
//! // Create agents with plugins
//! let agent = AgentBuilder::new("research-agent")
//!     .with_instructions("You are a research assistant")
//!     .with_openrouter("anthropic/claude-3-sonnet", &api_key)
//!     .build()?;
//! 
//! // Create project-scoped conversation
//! let conversation = project.create_conversation(vec![agent])?;
//! 
//! // Use conversation normally - it now has project context
//! let response = conversation.send("Analyze this data")?;
//! # Ok::<(), Box<dyn std::error::Error>>(())
//! ```
//! 
//! ## Advanced Usage with Multiple Conversations
//! 
//! ```rust,no_run
//! use hpd_rust_agent::{Project, AgentBuilder};
//! 
//! // Multi-conversation project
//! let project = Project::create("Complex Project", Some("./storage"))?;
//! 
//! let research_agent = AgentBuilder::new("researcher")
//!     .with_instructions("You are a research specialist")
//!     .build()?;
//! 
//! let analysis_agent = AgentBuilder::new("analyst") 
//!     .with_instructions("You are a data analyst")
//!     .build()?;
//! 
//! // Multiple conversations within same project context
//! let research_conv = project.create_conversation(vec![research_agent])?;
//! let analysis_conv = project.create_conversation(vec![analysis_agent])?;
//! 
//! // Both conversations share project-scoped memory and documents
//! research_conv.send("Research market trends")?;
//! analysis_conv.send("Analyze the research data")?; // Can access shared context
//! 
//! // Project metadata
//! let info = project.get_info()?;
//! println!("Project {} has {} conversations", info.name, info.conversation_count);
//! # Ok::<(), Box<dyn std::error::Error>>(())
//! ```

use crate::{ffi, agent::Agent, conversation::Conversation};
use serde::{Deserialize, Serialize};
use std::{mem, ffi::{c_void, CStr, CString}};

/// Project information including metadata and statistics
/// 
/// This structure contains all the metadata about a project, including
/// creation timestamps, conversation counts, and other project details.
#[derive(Debug, Deserialize)]
pub struct ProjectInfo {
    /// Unique project identifier (GUID)
    pub id: String,
    /// Human-readable project name
    pub name: String,
    /// Project description
    pub description: String,
    /// Number of conversations in this project
    pub conversation_count: i32,
    /// ISO 8601 timestamp when the project was created
    pub created_at: String,
    /// ISO 8601 timestamp of the last project activity
    pub last_activity: String,
}

/// A project that provides shared context and resource management for multiple conversations
/// 
/// Projects are the top-level organizational unit that contain multiple conversations.
/// All conversations within a project share the same memory space and document context,
/// enabling agents to access information from previous conversations within the same project.
/// 
/// ## Memory Management
/// 
/// Project handles are automatically cleaned up when the Project instance is dropped,
/// thanks to the `Drop` trait implementation. This ensures proper resource cleanup.
/// 
/// ## Thread Safety
/// 
/// Projects are `Send` and `Sync`, allowing them to be used across threads. The underlying
/// C# implementation handles thread safety.
pub struct Project {
    handle: *mut c_void,
}

impl Project {
    /// Creates a new project with the specified name and optional storage directory
    /// 
    /// This is the primary factory method for creating new projects. Projects provide
    /// shared context and resource management for multiple conversations.
    /// 
    /// ## Parameters
    /// 
    /// - `name`: A human-readable name for the project
    /// - `storage_directory`: Optional directory path for project storage. If `None`, 
    ///   uses the default storage location.
    /// 
    /// ## Returns
    /// 
    /// Returns a `Result<Project, String>` where:
    /// - `Ok(Project)` on successful creation
    /// - `Err(String)` with error message on failure
    /// 
    /// ## Examples
    /// 
    /// ```rust,no_run
    /// use hpd_rust_agent::Project;
    /// 
    /// // Create with default storage
    /// let project = Project::create("My Project", None)?;
    /// 
    /// // Create with custom storage directory
    /// let project = Project::create("My Project", Some("./custom/storage"))?;
    /// # Ok::<(), Box<dyn std::error::Error>>(())
    /// ```
    /// 
    /// ## Error Conditions
    /// 
    /// - Project name contains null bytes
    /// - Storage directory path contains null bytes  
    /// - C# side fails to create project (e.g., permissions, disk space)
    pub fn create(name: &str, storage_directory: Option<&str>) -> Result<Self, String> {
        let c_name = CString::new(name)
            .map_err(|_| "Project name contains null bytes".to_string())?;

        let storage_ptr = if let Some(storage) = storage_directory {
            let c_storage = CString::new(storage)
                .map_err(|_| "Storage directory contains null bytes".to_string())?;
            c_storage.as_ptr()
        } else {
            std::ptr::null()
        };

        let project_handle = unsafe {
            ffi::create_project(c_name.as_ptr(), storage_ptr)
        };

        if project_handle.is_null() {
            Err("Failed to create project on C# side".to_string())
        } else {
            Ok(Self { handle: project_handle })
        }
    }

    /// Creates a conversation within this project using the provided agents
    /// 
    /// This method creates a new conversation that is scoped to this project.
    /// The conversation will have access to the project's shared memory and context,
    /// allowing agents to reference information from other conversations within
    /// the same project.
    /// 
    /// ## Parameters
    /// 
    /// - `agents`: A vector of `Agent` instances to participate in the conversation.
    ///   Must contain at least one agent.
    /// 
    /// ## Returns
    /// 
    /// Returns a `Result<Conversation, String>` where:
    /// - `Ok(Conversation)` on successful creation
    /// - `Err(String)` with error message on failure
    /// 
    /// ## Examples
    /// 
    /// ```rust,no_run
    /// use hpd_rust_agent::{Project, AgentBuilder};
    /// 
    /// let project = Project::create("My Project", None)?;
    /// 
    /// let agent = AgentBuilder::new("assistant")
    ///     .with_instructions("You are a helpful assistant")
    ///     .build()?;
    /// 
    /// // Single agent conversation
    /// let conversation = project.create_conversation(vec![agent])?;
    /// 
    /// // Multi-agent conversation (when supported)
    /// // let conversation = project.create_conversation(vec![agent1, agent2])?;
    /// # Ok::<(), Box<dyn std::error::Error>>(())
    /// ```
    /// 
    /// ## Error Conditions
    /// 
    /// - Empty agents vector
    /// - Invalid agent handles
    /// - C# side fails to create conversation
    /// 
    /// ## Memory Management
    /// 
    /// This method takes ownership of the agents vector. The agents will be
    /// managed by the C# side for the lifetime of the conversation.
    pub fn create_conversation(&self, agents: Vec<Agent>) -> Result<Conversation, String> {
        if agents.is_empty() {
            return Err("At least one agent is required to create a conversation".to_string());
        }

        let agent_handles: Vec<*mut c_void> = agents.iter().map(|a| a.handle).collect();

        let conversation_handle = unsafe {
            ffi::project_create_conversation(
                self.handle,
                agent_handles.as_ptr(),
                agent_handles.len() as i32,
            )
        };

        // Prevent Rust from dropping the agents now that C# holds a reference
        mem::forget(agents);

        if conversation_handle.is_null() {
            Err("Failed to create conversation on C# side".to_string())
        } else {
            Ok(Conversation::from_handle(conversation_handle))
        }
    }

    /// Gets project information including metadata and statistics
    /// 
    /// This method retrieves comprehensive information about the project,
    /// including its unique ID, name, creation timestamp, conversation count,
    /// and last activity timestamp.
    /// 
    /// ## Returns
    /// 
    /// Returns a `Result<ProjectInfo, String>` where:
    /// - `Ok(ProjectInfo)` containing project metadata on success
    /// - `Err(String)` with error message on failure
    /// 
    /// ## Examples
    /// 
    /// ```rust,no_run
    /// use hpd_rust_agent::Project;
    /// 
    /// let project = Project::create("Analytics Project", None)?;
    /// 
    /// let info = project.get_info()?;
    /// println!("Project ID: {}", info.id);
    /// println!("Name: {}", info.name);
    /// println!("Conversations: {}", info.conversation_count);
    /// println!("Created: {}", info.created_at);
    /// println!("Last Activity: {}", info.last_activity);
    /// # Ok::<(), Box<dyn std::error::Error>>(())
    /// ```
    /// 
    /// ## Error Conditions
    /// 
    /// - Invalid project handle
    /// - C# side fails to retrieve project information
    /// - JSON parsing errors (internal)
    /// 
    /// ## Data Format
    /// 
    /// Timestamps are returned in ISO 8601 format (e.g., "2024-01-15T10:30:45.123Z").
    /// The project ID is a GUID string without dashes.
    pub fn get_info(&self) -> Result<ProjectInfo, String> {
        let info_ptr = unsafe {
            ffi::get_project_info(self.handle)
        };

        if info_ptr.is_null() {
            return Err("Failed to get project info from C# side".to_string());
        }

        let c_str = unsafe { CStr::from_ptr(info_ptr) };
        let json_str = c_str.to_str()
            .map_err(|_| "Project info contains invalid UTF-8".to_string())?;

        let project_info: ProjectInfo = serde_json::from_str(json_str)
            .map_err(|e| format!("Failed to parse project info JSON: {}", e))?;

        // Free the string allocated by C#
        unsafe { ffi::free_string(info_ptr as *mut c_void) };

        Ok(project_info)
    }
}

impl Drop for Project {
    fn drop(&mut self) {
        if !self.handle.is_null() {
            unsafe { ffi::destroy_project(self.handle) };
            self.handle = std::ptr::null_mut();
        }
    }
}

// Send and Sync are safe because the C# side manages thread safety
unsafe impl Send for Project {}
unsafe impl Sync for Project {}