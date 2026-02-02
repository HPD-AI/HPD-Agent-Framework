<script lang="ts">
	import { boxWith, mergeProps } from 'svelte-toolbelt';
	import { PermissionDialogActionsState } from '../permission-dialog.svelte.js';
	import type { PermissionDialogActionsProps } from '../types.js';
	import { createId } from '$lib/internal/create-id.js';

	const uid = $props.id();

	let {
		id = createId(uid),
		children,
		child,
		ref = $bindable(null),
		...restProps
	}: PermissionDialogActionsProps = $props();

	const actionsState = PermissionDialogActionsState.create({
		id: boxWith(() => id),
		ref: boxWith(
			() => ref,
			(v) => (ref = v)
		)
	});

	const mergedProps = $derived(mergeProps(restProps, actionsState.props));

	// Snippet props for customization
	const snippetProps = $derived.by(() => ({
		approve: actionsState.root.approve.bind(actionsState.root),
		deny: actionsState.root.deny.bind(actionsState.root)
	}));
</script>

{#if child}
	{@render child({ props: mergedProps, ...snippetProps })}
{:else}
	<div {...mergedProps}>
		{@render children?.(snippetProps)}
	</div>
{/if}
