<script lang="ts">
	import { mergeProps, boxWith } from 'svelte-toolbelt';
	import type { RunConfigModelSelectorProps } from '../types.js';
	import { RunConfigModelSelectorState } from '../run-config.svelte.js';

	let {
		runConfig,
		providers = [],
		disabled = false,
		child,
		children,
		...restProps
	}: RunConfigModelSelectorProps = $props();

	const state = RunConfigModelSelectorState.create({
		runConfig: boxWith(() => runConfig),
		providers: boxWith(() => providers),
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
