<script lang="ts">
	import { mergeProps, boxWith } from 'svelte-toolbelt';
	import type { RunConfigSystemInstructionsInputProps } from '../types.js';
	import { RunConfigSystemInstructionsInputState } from '../run-config.svelte.js';

	let {
		runConfig,
		disabled = false,
		child,
		children,
		...restProps
	}: RunConfigSystemInstructionsInputProps = $props();

	const state = RunConfigSystemInstructionsInputState.create({
		runConfig: boxWith(() => runConfig),
		disabled: boxWith(() => disabled),
	});

	const mergedProps = $derived(mergeProps(restProps, state.props));
</script>

{#if child}
	{@render child({ props: mergedProps, ...state.snippetProps })}
{:else}
	<div {...mergedProps}>
		{@render children?.(state.snippetProps)}
	</div>
{/if}
