<script lang="ts">
	import { boxWith, mergeProps } from 'svelte-toolbelt';
	import { PermissionDialogDescriptionState } from '../permission-dialog.svelte.js';
	import type { PermissionDialogDescriptionProps } from '../types.js';
	import { createId } from '$lib/internal/create-id.js';

	const uid = $props.id();

	let {
		id = createId(uid),
		children,
		child,
		ref = $bindable(null),
		...restProps
	}: PermissionDialogDescriptionProps = $props();

	const descriptionState = PermissionDialogDescriptionState.create({
		id: boxWith(() => id),
		ref: boxWith(
			() => ref,
			(v) => (ref = v)
		)
	});

	const mergedProps = $derived(mergeProps(restProps, descriptionState.props));

	// Snippet props for customization
	const snippetProps = $derived.by(() => ({
		description: descriptionState.root.currentRequest?.description,
		arguments: descriptionState.root.currentRequest?.arguments,
		functionName: descriptionState.root.currentRequest?.functionName,
		status: descriptionState.root.status
	}));
</script>

{#if child}
	{@render child({ props: mergedProps, ...snippetProps })}
{:else}
	<div {...mergedProps}>
		{@render children?.(snippetProps)}
	</div>
{/if}
