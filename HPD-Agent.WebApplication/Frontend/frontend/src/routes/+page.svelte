<script lang="ts">
	import {
		createWorkspace,
		Message,
		MessageActions,
		MessageEdit,
		MessageList,
		Input,
		ToolExecution,
		SessionList,
		type Workspace,
	} from '@hpd/hpd-agent-headless-ui';
	import Artifact from '$lib/artifacts/Artifact.svelte';
	import { artifactStore, type ArtifactState } from '$lib/artifacts/artifact-store.js';
	import { artifactTool, handleArtifactTool } from '$lib/artifacts/artifact-plugin.js';

	const API_BASE = 'http://localhost:5135';

	let currentMessage = $state('');
	let editingIndex = $state<number | null>(null);

	// Artifact state (reactive)
	let artifact = $state<ArtifactState>({
		isOpen: false,
		content: '',
		language: 'html',
		title: '',
		type: 'code',
	});
	artifactStore.subscribe((value) => (artifact = value));

	const workspace: Workspace = createWorkspace({
		baseUrl: API_BASE,
		clientToolGroups: [artifactTool],
		onClientToolInvoke: async (request) => {
			console.log('[TOOL INVOKE]', request.toolName, request.arguments);
			const response = handleArtifactTool(
				request.toolName,
				request.arguments,
				request.requestId
			);
			console.log('[TOOL RESPONSE]', response);
			return response;
		},
		onError: (message) => {
			console.error('[AGENT ERROR]', message);
		},
		onComplete: () => {
			console.log('[AGENT] Turn complete');
		},
	});

	async function sendMessage(details: { value: string }) {
		if (!workspace.state || workspace.state.streaming) return;
		currentMessage = '';
		await workspace.send(details.value);
	}
</script>

<div class="h-screen flex bg-gray-50">
	<!-- Sidebar: Session List -->
	<div class="w-60 flex-shrink-0 bg-white border-r flex flex-col">
		<div class="px-4 py-3 border-b">
			<h2 class="text-sm font-semibold text-gray-700">Sessions</h2>
		</div>

		<SessionList.Root
			sessions={workspace.sessions}
			activeSessionId={workspace.activeSessionId}
			loading={workspace.loading}
			onSelect={(id) => workspace.selectSession(id)}
			onDelete={(id) => workspace.deleteSession(id)}
			onCreate={() => workspace.createSession()}
			class="flex-1 overflow-y-auto"
		>
			{#snippet children({ sessions, isEmpty, loading: listLoading })}
				<div class="p-2">
					<SessionList.CreateButton
						class="w-full mb-2 px-3 py-2 text-sm bg-blue-500 text-white rounded-md hover:bg-blue-600 text-left"
					>
						+ New Session
					</SessionList.CreateButton>

					{#if listLoading}
						<p class="text-xs text-gray-400 px-2 py-4 text-center">Loading...</p>
					{:else if isEmpty}
						<SessionList.Empty class="text-xs text-gray-400 px-2 py-4 text-center">
							No sessions yet
						</SessionList.Empty>
					{:else}
						{#each sessions as session (session.id)}
							<SessionList.Item {session} class="w-full">
								{#snippet children({ isActive, lastActivity })}
									<div
										class="w-full text-left px-3 py-2 rounded-md text-sm cursor-pointer
											{isActive
											? 'bg-blue-50 text-blue-700 font-medium'
											: 'text-gray-600 hover:bg-gray-100'}"
									>
										<div class="truncate font-mono">
											{session.id.substring(0, 8)}...
										</div>
										<div class="text-xs opacity-60 mt-0.5">{lastActivity}</div>
									</div>
								{/snippet}
							</SessionList.Item>
						{/each}
					{/if}
				</div>
			{/snippet}
		</SessionList.Root>
	</div>

	<!-- Main Content -->
	<div class="flex-1 flex flex-col overflow-hidden">
		<!-- Header -->
		<div class="bg-white border-b px-6 py-3 flex items-center flex-shrink-0">
			<div>
				<h1 class="text-lg font-semibold">HPD-Agent Chat</h1>
				<p class="text-xs text-gray-500">
					{workspace.state?.messages.length ?? 0} messages
					{#if workspace.activeBranchId}
						• Branch: {workspace.activeBranchId}
					{/if}
				</p>
			</div>
		</div>

		<!-- Chat + Artifact -->
		<div class="flex-1 flex overflow-hidden">
			<!-- Chat Panel -->
			<div class="flex-1 flex flex-col {artifact.isOpen ? 'border-r' : ''}">
				<!-- Messages -->
				{#if workspace.state}
					<MessageList.Root
						messages={workspace.state.messages}
						autoScroll={true}
						keyboardNav={false}
						aria-label="Chat conversation"
						class="flex-1 overflow-y-auto p-6"
					>
						{#each workspace.state.messages as message, i (message.id)}
							<Message {message}>
								{#snippet children({ content, role, streaming, reasoning, hasReasoning, status, toolCalls })}
									<div class="flex gap-3 {role === 'user' ? 'justify-end' : ''} mb-4">
										<MessageEdit.Root
											{workspace}
											messageIndex={i}
											initialValue={content}
											editing={editingIndex === i}
											onStartEdit={() => { editingIndex = i; }}
											onSave={() => { editingIndex = null; }}
											onCancel={() => { editingIndex = null; }}
											class="group max-w-[80%] {role === 'user'
												? 'bg-blue-500 text-white'
												: 'bg-white border'} rounded-lg p-4"
										>
											{#snippet children({ editing: isEditing, startEdit, save, cancel, pending })}
												<!-- Status Badge -->
												{#if role === 'assistant'}
													<div class="text-xs mb-2 flex items-center gap-2">
														<span class="font-semibold opacity-70">Status:</span>
														<span
															class="px-2 py-0.5 rounded"
															class:bg-blue-100={status === 'streaming'}
															class:bg-purple-100={status === 'thinking'}
															class:bg-green-100={status === 'complete'}
															class:text-blue-700={status === 'streaming'}
															class:text-purple-700={status === 'thinking'}
															class:text-green-700={status === 'complete'}
														>
															{status}
														</span>
													</div>
												{/if}

												<!-- Reasoning -->
												{#if hasReasoning}
													<div
														class="text-sm italic opacity-70 mb-2 bg-purple-50 border-l-2 border-purple-500 pl-2 py-1"
													>
														<strong>Thinking:</strong>
														{reasoning}
													</div>
												{/if}

												<!-- Tool Calls -->
												{#if toolCalls && toolCalls.length > 0}
													<div class="mb-3 space-y-2">
														{#each toolCalls as toolCall (toolCall.callId)}
															<ToolExecution.Root {toolCall} class="bg-gray-50 rounded border border-gray-200 overflow-hidden text-gray-900">
																<ToolExecution.Trigger class="w-full text-left px-3 py-2 hover:bg-gray-100 transition-colors flex items-center justify-between">
																	{#snippet children({ expanded })}
																		<div class="flex items-center gap-2 flex-1">
																			<span class="text-sm">🔧</span>
																			<span class="text-sm font-medium">{toolCall.name}</span>
																			<ToolExecution.Status>
																				{#snippet children({ status: toolStatus, isActive })}
																					<span class="text-xs px-2 py-0.5 rounded {isActive ? 'bg-blue-100 text-blue-700' : 'bg-gray-100 text-gray-600'}">
																						{toolStatus}
																					</span>
																				{/snippet}
																			</ToolExecution.Status>
																		</div>
																		<span class="text-gray-400 transition-transform {expanded ? 'rotate-180' : ''}">▼</span>
																	{/snippet}
																</ToolExecution.Trigger>

																<ToolExecution.Content class="border-t border-gray-200">
																	<div class="p-3 space-y-2">
																		<ToolExecution.Args>
																			{#snippet children({ argsJson, hasArgs })}
																				{#if hasArgs}
																					<div>
																						<div class="text-xs font-semibold text-gray-600 mb-1">Arguments:</div>
																						<pre class="text-xs bg-gray-100 text-gray-800 p-2 rounded overflow-auto">{argsJson}</pre>
																					</div>
																				{/if}
																			{/snippet}
																		</ToolExecution.Args>

																		<ToolExecution.Result>
																			{#snippet children({ result, error, hasResult, hasError })}
																				{#if hasError}
																					<div>
																						<div class="text-xs font-semibold text-red-600 mb-1">Error:</div>
																						<pre class="text-xs bg-red-50 text-red-700 p-2 rounded overflow-auto">{error}</pre>
																					</div>
																				{:else if hasResult}
																					<div>
																						<div class="text-xs font-semibold text-green-600 mb-1">Result:</div>
																						<pre class="text-xs bg-green-50 text-green-700 p-2 rounded overflow-auto">{result}</pre>
																					</div>
																				{/if}
																			{/snippet}
																		</ToolExecution.Result>
																	</div>
																</ToolExecution.Content>
															</ToolExecution.Root>
														{/each}
													</div>
												{/if}

												<!-- Edit UI (shown when editing) -->
												{#if isEditing}
													<MessageEdit.Textarea
														placeholder="Edit your message…"
														aria-label="Edit message"
														class="w-full text-sm {role === 'user' ? 'bg-blue-400 text-white placeholder-blue-200' : 'bg-gray-50 text-gray-900'} rounded border-0 focus:ring-0 resize-none p-0 mb-2"
													/>
													<div class="flex gap-2 justify-end mt-2">
														<MessageEdit.CancelButton
															aria-label="Cancel edit"
															class="text-xs px-3 py-1 rounded {role === 'user' ? 'hover:bg-white/20 text-white' : 'hover:bg-gray-100 text-gray-500'}"
														>
															Cancel
														</MessageEdit.CancelButton>
														<MessageEdit.SaveButton
															aria-label="Save edit"
															class="text-xs px-3 py-1 rounded bg-white/20 {role === 'user' ? 'text-white hover:bg-white/30' : 'text-blue-600 bg-blue-50 hover:bg-blue-100'} disabled:opacity-50"
														>
															{#snippet children({ pending: savePending })}
																{savePending ? '…' : 'Save & Send'}
															{/snippet}
														</MessageEdit.SaveButton>
													</div>
												{:else}
													<!-- Content (view mode) -->
													<div class="whitespace-pre-wrap">
														{content}
														{#if streaming}
															<span class="inline-block w-1.5 h-4 bg-current ml-1 animate-pulse">▊</span>
														{/if}
													</div>
												{/if}

												<!-- MessageActions toolbar (view mode only) -->
												{#if !isEditing}
													<MessageActions.Root
														{workspace}
														messageIndex={i}
														{role}
														branch={workspace.activeBranch}
													>
														{#snippet children({ hasSiblings, pending: actionsPending, position })}
															<div class="flex items-center justify-end gap-1 mt-2 opacity-0 group-hover:opacity-100 transition-opacity">
																<MessageActions.CopyButton content={content} aria-label="Copy message">
																	{#snippet children({ copy, copied })}
																		<button
																			onclick={copy}
																			class="text-xs px-2 py-1 rounded {role === 'user' ? 'hover:bg-white/20 text-white' : 'hover:bg-gray-100 text-gray-500'}"
																		>{copied ? '✓' : 'Copy'}</button>
																	{/snippet}
																</MessageActions.CopyButton>
																<MessageActions.EditButton aria-label="Edit message">
																	{#snippet children({ disabled })}
																		{#if !disabled}
																			<button
																				onclick={startEdit}
																				class="text-xs px-2 py-1 rounded {role === 'user' ? 'hover:bg-white/20 text-white' : 'hover:bg-gray-100 text-gray-500'}"
																			>Edit</button>
																		{/if}
																	{/snippet}
																</MessageActions.EditButton>
																<MessageActions.RetryButton aria-label="Retry">
																	{#snippet children({ retry, disabled: retryDisabled, status: retryStatus })}
																		{#if !retryDisabled}
																			<button
																				onclick={retry}
																				class="text-xs px-2 py-1 rounded {role === 'user' ? 'hover:bg-white/20 text-white' : 'hover:bg-gray-100 text-gray-500'}"
																			>{retryStatus === 'pending' ? '…' : '↺ Retry'}</button>
																		{/if}
																	{/snippet}
																</MessageActions.RetryButton>
																{#if hasSiblings}
																	<div class="flex items-center gap-0.5 ml-1">
																		<MessageActions.Prev
																			aria-label="Previous version"
																			onclick={() => workspace.activeBranch?.previousSiblingId && workspace.switchBranch(workspace.activeBranch.previousSiblingId)}
																			class="text-xs px-1.5 py-1 rounded {role === 'user' ? 'hover:bg-white/20 text-white disabled:opacity-30' : 'hover:bg-gray-100 text-gray-500 disabled:opacity-30'}"
																		>◀</MessageActions.Prev>
																		<MessageActions.Position class="text-xs min-w-[3rem] text-center {role === 'user' ? 'text-white/80' : 'text-gray-500'}" />
																		<MessageActions.Next
																			aria-label="Next version"
																			onclick={() => workspace.activeBranch?.nextSiblingId && workspace.switchBranch(workspace.activeBranch.nextSiblingId)}
																			class="text-xs px-1.5 py-1 rounded {role === 'user' ? 'hover:bg-white/20 text-white disabled:opacity-30' : 'hover:bg-gray-100 text-gray-500 disabled:opacity-30'}"
																		>▶</MessageActions.Next>
																	</div>
																{/if}
															</div>
														{/snippet}
													</MessageActions.Root>
												{/if}
											{/snippet}
										</MessageEdit.Root>
									</div>
								{/snippet}
							</Message>
						{/each}
					</MessageList.Root>
				{:else}
					<div class="flex-1 flex items-center justify-center text-gray-400 text-sm">
						{workspace.loading ? 'Loading...' : 'Select or create a session'}
					</div>
				{/if}

				<!-- Input -->
				<div class="bg-white border-t p-4 flex-shrink-0">
					<div class="flex gap-2">
						<Input.Root
							value={currentMessage}
							onChange={(details) => { currentMessage = details.value; }}
							onSubmit={sendMessage}
							placeholder="Type a message... (Enter to send, Shift+Enter for new line)"
							disabled={!workspace.state || workspace.state.streaming}
							maxRows={5}
							class="flex-1 px-4 py-2 border rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:opacity-50"
						/>
						<button
							onclick={() => sendMessage({ value: currentMessage })}
							disabled={!currentMessage.trim() || !workspace.state || workspace.state.streaming}
							class="px-6 py-2 bg-blue-500 text-white rounded-md hover:bg-blue-600 disabled:opacity-50"
						>
							{workspace.state?.streaming ? '...' : 'Send'}
						</button>
					</div>
				</div>
			</div>

			<!-- Artifact Panel -->
			{#if artifact.isOpen}
				<div class="w-2/3 flex flex-col flex-shrink-0">
					<Artifact />
				</div>
			{/if}
		</div>
	</div>
</div>

<!-- Permission Dialog -->
{#if workspace.state && workspace.state.pendingPermissions.length > 0}
	{@const permission = workspace.state.pendingPermissions[0]}
	<div class="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
		<div class="bg-white rounded-lg shadow-xl p-6 max-w-md w-full">
			<h3 class="text-lg font-semibold mb-2">Permission Required</h3>
			<p class="text-sm text-gray-600 mb-4">
				The agent wants to use: <strong>{permission.functionName}</strong>
			</p>
			{#if permission.description}
				<p class="text-sm text-gray-500 mb-4">{permission.description}</p>
			{/if}
			{#if permission.arguments}
				<pre class="text-xs bg-gray-100 p-2 rounded mb-4 overflow-auto">
{JSON.stringify(permission.arguments, null, 2)}</pre>
			{/if}
			<div class="flex gap-2">
				<button
					onclick={() => workspace.approve(permission.permissionId, 'ask')}
					class="flex-1 px-4 py-2 bg-blue-500 text-white rounded-md hover:bg-blue-600"
				>
					Allow Once
				</button>
				<button
					onclick={() => workspace.approve(permission.permissionId, 'allow_always')}
					class="flex-1 px-4 py-2 bg-green-500 text-white rounded-md hover:bg-green-600"
				>
					Always Allow
				</button>
				<button
					onclick={() => workspace.deny(permission.permissionId, 'User denied')}
					class="flex-1 px-4 py-2 bg-red-600 text-white rounded-md hover:bg-red-700"
				>
					Never Allow
				</button>
			</div>
		</div>
	</div>
{/if}
