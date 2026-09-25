import { Component, type ErrorInfo, type ReactNode } from 'react';
import { Bug } from 'lucide-react';
import { Button, CodeBlock, EmptyState } from '@/components/ui';

interface State {
  error: Error | null;
  key?: string;
}

/** Keeps a rendering bug on one page from blanking the whole console. */
export class ErrorBoundary extends Component<{ children: ReactNode; resetKey?: string }, State> {
  override state: State = { error: null, key: this.props.resetKey };

  static getDerivedStateFromError(error: Error): Partial<State> {
    return { error };
  }

  static getDerivedStateFromProps(props: { resetKey?: string }, state: State): Partial<State> | null {
    if (props.resetKey !== state.key) return { key: props.resetKey, error: null };
    return null;
  }

  override componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('UI error', error, info.componentStack);
  }

  override render() {
    if (!this.state.error) return this.props.children;
    const isChunkError = /Failed to fetch dynamically imported module|Importing a module script failed/i.test(
      this.state.error.message,
    );
    return (
      <div className="mx-auto max-w-2xl">
        <EmptyState
          icon={<Bug size={18} />}
          title={isChunkError ? 'The management UI was updated' : 'This page failed to render'}
          description={
            isChunkError
              ? 'Reload the page to load the new version.'
              : 'An unexpected error occurred in the user interface. Reloading usually helps; if it persists, include the details below when reporting it.'
          }
          action={
            <Button variant="primary" onClick={() => window.location.reload()}>
              Reload page
            </Button>
          }
        />
        {!isChunkError && <CodeBlock code={this.state.error.stack ?? this.state.error.message} title="Details" wrap maxHeight={240} />}
      </div>
    );
  }
}
