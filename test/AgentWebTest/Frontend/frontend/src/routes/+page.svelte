<script lang="ts">
	import { onMount } from 'svelte';
	import {
		createAgent,
		Message,
		MessageList,
		Input,
		ToolExecution,
		type Agent,
	} from '@hpd/hpd-agent-headless-ui';
	import Artifact from '$lib/artifacts/Artifact.svelte';
	import { artifactStore, type ArtifactState } from '$lib/artifacts/artifact-store.js';
	import { artifactTool, handleArtifactTool } from '$lib/artifacts/artifact-plugin.js';

	const API_BASE = 'http://localhost:5135';

	let conversationId = $state<string | null>(null);
	let currentMessage = $state('');
	let agent = $state<Agent | null>(null);

	// Artifact state (reactive)
	let artifact = $state<ArtifactState>({
		isOpen: false,
		content: '',
		language: 'html',
		title: '',
		highlightedLines: [],
	});
	artifactStore.subscribe((value) => (artifact = value));

	onMount(async () => {
		const saved = localStorage.getItem('conversationId');
		if (saved) {
			try {
				const res = await fetch(`${API_BASE}/conversations/${saved}`);
				if (res.ok) {
					conversationId = saved;
					initializeAgent(saved);
					return;
				}
			} catch (e) {
				console.error('Failed to verify conversation:', e);
			}
		}
		await createConversation();
	});

	async function createConversation() {
		const res = await fetch(`${API_BASE}/conversations`, { method: 'POST' });
		const data = await res.json();
		conversationId = data.id;
		console.log('[CREATE] Created new conversation:', conversationId);
		localStorage.setItem('conversationId', conversationId!);
		artifactStore.close();
		if (conversationId) {
			initializeAgent(conversationId);
		}
	}

	function initializeAgent(convId: string) {
		agent = createAgent({
			baseUrl: API_BASE,
			conversationId: convId,
			clientToolGroups: [artifactTool],
			onClientToolInvoke: async (request) => {
				console.log('[TOOL INVOKE]', request.toolName, request.arguments);

				// Handle artifact tools
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
		console.log('[AGENT] Created agent:', agent);
	}

	async function sendMessage(details: { value: string }) {
		if (!agent || !conversationId) return;

		console.log('[SEND] Sending message to conversation:', conversationId);

		// Clear the input BEFORE sending (don't wait for streaming)
		currentMessage = '';

		// Send the message (this will stream and take time)
		await agent.send(details.value);
	}
</script>

<div class="h-screen flex flex-col bg-gray-50">
	<!-- Header -->
	<div class="bg-white border-b px-6 py-4 flex justify-between items-center">
		<div>
			<h1 class="text-xl font-semibold">HPD-Agent Chat (createAgent API)</h1>
			<p class="text-sm text-gray-500">{agent?.state.messages.length ?? 0} messages</p>
		</div>
		<button
			onclick={createConversation}
			class="px-4 py-2 bg-gray-100 hover:bg-gray-200 rounded-md text-sm"
		>
			New Chat
		</button>
	</div>

	<!-- Main Content -->
	<div class="flex-1 flex overflow-hidden">
		<!-- Chat Panel -->
		<div class="flex-1 flex flex-col {artifact.isOpen ? 'w-1/3 min-w-[300px] border-r' : ''}">
			<!-- Messages using MessageList -->
			{#if agent}
				<MessageList.Root
					messages={agent.state.messages}
					autoScroll={true}
					keyboardNav={false}
					aria-label="Chat conversation"
					class="flex-1 overflow-y-auto p-6"
				>
					{#each agent.state.messages as message (message.id)}
						<Message {message}>
							{#snippet children({ content, role, streaming, thinking, reasoning, hasReasoning, status, toolCalls })}
								{@const debugToolCalls = console.log(`[MESSAGE ${message.id}] toolCalls:`, toolCalls?.length ?? 0, toolCalls)}
								<div
									class="flex gap-3 {role === 'user' ? 'justify-end' : ''} mb-4"
								>
									<div
										class="max-w-[80%] {role === 'user'
											? 'bg-blue-500 text-white'
											: 'bg-white border'} rounded-lg p-4"
									>
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

										<!-- Content -->
										<div class="whitespace-pre-wrap">
											{content}
											{#if streaming}
												<span
													class="inline-block w-1.5 h-4 bg-current ml-1 animate-pulse"
													>▊</span
												>
											{/if}
										</div>
									</div>
								</div>
							{/snippet}
						</Message>
					{/each}
				</MessageList.Root>
			{/if}

			<!-- Input using Input component -->
			<div class="bg-white border-t p-4">
				<div class="flex gap-2">
					<Input.Root
						value={currentMessage}
						onChange={(details) => {
							currentMessage = details.value;
						}}
						onSubmit={sendMessage}
						placeholder="Type a message... (Enter to send, Shift+Enter for new line)"
						disabled={!agent || agent.state.streaming}
						maxRows={5}
						class="flex-1 px-4 py-2 border rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:opacity-50"
					/>
					<button
						onclick={() => sendMessage({ value: currentMessage })}
						disabled={!currentMessage.trim() || !agent || agent.state.streaming}
						class="px-6 py-2 bg-blue-500 text-white rounded-md hover:bg-blue-600 disabled:opacity-50"
					>
						{agent?.state.streaming ? '...' : 'Send'}
					</button>
				</div>
			</div>
		</div>

		<!-- Artifact Panel -->
		{#if artifact.isOpen}
			<div class="w-2/3 flex flex-col">
				<Artifact />
			</div>
		{/if}
	</div>
</div>

<!-- Permission Dialog -->
{#if agent && agent.state.pendingPermissions.length > 0}
	{@const permission = agent.state.pendingPermissions[0]}
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
					onclick={() => agent?.approve(permission.permissionId, 'ask')}
					class="flex-1 px-4 py-2 bg-blue-500 text-white rounded-md hover:bg-blue-600"
				>
					Allow Once
				</button>
				<button
					onclick={() => agent?.approve(permission.permissionId, 'allow_always')}
					class="flex-1 px-4 py-2 bg-green-500 text-white rounded-md hover:bg-green-600"
				>
					Always Allow
				</button>
				<button
					onclick={() => agent?.deny(permission.permissionId, 'User denied')}
					class="flex-1 px-4 py-2 bg-red-600 text-white rounded-md hover:bg-red-700"
				>
					Never Allow
				</button>
			</div>
		</div>
	</div>
{/if}
