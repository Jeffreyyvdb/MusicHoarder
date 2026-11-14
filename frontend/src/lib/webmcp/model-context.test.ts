import { describe, expect, it, vi } from 'vitest';
import { findModelContext, registerTools, type ModelContextTool } from './model-context.ts';

function fakeContext() {
  const tools = new Map<string, ModelContextTool>();
  return {
    tools,
    registerTool: vi.fn(async (tool: ModelContextTool, options?: { signal?: AbortSignal }) => {
      if (tools.has(tool.name)) throw new DOMException('taken', 'InvalidStateError');
      tools.set(tool.name, tool);
      options?.signal?.addEventListener('abort', () => tools.delete(tool.name));
    })
  };
}

const tool = (name: string): ModelContextTool => ({
  name,
  description: name,
  execute: async () => ({})
});

describe('findModelContext', () => {
  it('prefers document.modelContext, the current home of the API', () => {
    const onDocument = fakeContext();
    const onNavigator = fakeContext();
    expect(findModelContext({ modelContext: onDocument }, { modelContext: onNavigator })).toBe(
      onDocument
    );
  });

  it('falls back to navigator.modelContext, where earlier Chrome builds put it', () => {
    const onNavigator = fakeContext();
    expect(findModelContext({}, { modelContext: onNavigator })).toBe(onNavigator);
  });

  it('is null in a browser without WebMCP, or with something else under the name', () => {
    expect(findModelContext({}, {})).toBeNull();
    expect(findModelContext({ modelContext: {} }, { modelContext: 1 })).toBeNull();
    expect(findModelContext(undefined, undefined)).toBeNull();
  });
});

describe('registerTools', () => {
  it('registers every tool and unregisters them all when the signal aborts', async () => {
    const context = fakeContext();
    const controller = new AbortController();
    const names = await registerTools(context, [tool('a'), tool('b')], controller.signal);
    expect(names).toEqual(['a', 'b']);
    expect([...context.tools.keys()]).toEqual(['a', 'b']);
    controller.abort();
    expect(context.tools.size).toBe(0);
  });

  it('skips a tool the browser refuses without dropping the others', async () => {
    const context = fakeContext();
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const names = await registerTools(
      context,
      [tool('a'), tool('a'), tool('bad name!'), tool('c')],
      new AbortController().signal
    );
    expect(names).toEqual(['a', 'c']);
    expect(warn).toHaveBeenCalledTimes(2);
    warn.mockRestore();
  });

  it('registers nothing more once the signal has aborted', async () => {
    const context = fakeContext();
    const controller = new AbortController();
    controller.abort();
    expect(await registerTools(context, [tool('a')], controller.signal)).toEqual([]);
    expect(context.registerTool).not.toHaveBeenCalled();
  });
});
