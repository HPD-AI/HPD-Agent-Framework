<script module>
	import { defineMeta } from '@storybook/addon-svelte-csf';
	import MessageListDemo from './MessageListDemo.svelte';

	const { Story } = defineMeta({
		title: 'Components/MessageList',
		component: MessageListDemo,
		tags: ['autodocs'],
		argTypes: {
			messageCount: {
				control: { type: 'number', min: 0, max: 20 },
				description: 'Number of messages to display'
			},
			autoScroll: {
				control: 'boolean',
				description: 'Auto-scroll to bottom when new messages arrive'
			},
			streaming: {
				control: 'boolean',
				description: 'Show streaming indicator on last message'
			}
		}
	});
</script>

<!-- Default Story -->
<Story
	name="Default"
	args={{
		messageCount: 5,
		autoScroll: true,
		streaming: false
	}}
/>

<!-- Empty List -->
<Story
	name="Empty List"
	args={{
		messageCount: 0,
		autoScroll: true,
		streaming: false
	}}
/>

<!-- Single Message -->
<Story
	name="Single Message"
	args={{
		messageCount: 1,
		autoScroll: true,
		streaming: false
	}}
/>

<!-- Long Conversation -->
<Story
	name="Long Conversation"
	args={{
		messageCount: 15,
		autoScroll: true,
		streaming: false
	}}
/>

<!-- With Streaming -->
<Story
	name="Active Streaming"
	args={{
		messageCount: 6,
		autoScroll: true,
		streaming: true
	}}
/>

<!-- Auto-scroll Disabled -->
<Story
	name="No Auto-scroll"
	args={{
		messageCount: 10,
		autoScroll: false,
		streaming: false
	}}
/>

<!-- Few Messages -->
<Story
	name="Short Chat"
	args={{
		messageCount: 3,
		autoScroll: true,
		streaming: false
	}}
/>
