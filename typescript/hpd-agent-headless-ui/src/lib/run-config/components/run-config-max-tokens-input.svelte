<script lang="ts">
	import { mergeProps, boxWith } from 'svelte-toolbelt';
	import type { RunConfigMaxTokensInputProps } from '../types.js';
	import { RunConfigMaxTokensInputState } from '../run-config.svelte.js';

	let {
		runConfig,
		min = 1,
		max = undefined,
		disabled = false,
		child,
		children,
		...restProps
	}: RunConfigMaxTokensInputProps = $props();

	const state = RunConfigMaxTokensInputState.create({
		runConfig: boxWith(() => runConfig),
		min: boxWith(() => min),
		max: boxWith(() => max),
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
