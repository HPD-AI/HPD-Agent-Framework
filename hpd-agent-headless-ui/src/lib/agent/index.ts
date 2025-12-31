/**
 * Agent - Entry Point
 *
 * For Phase 1, we export the mock agent helper since we don't have
 * real HPD backend integration yet.
 */

export * from './exports.js';
export { createMockAgent } from '../testing/mock-agent.js';
export type { MockAgentOptions } from '../testing/mock-agent.js';
