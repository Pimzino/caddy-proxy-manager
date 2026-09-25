import { useEffect, useState } from 'react';
import { Check, Copy } from 'lucide-react';
import { copyText } from '@/lib/clipboard';
import { Button, type ButtonSize } from './Button';
import { useToast } from './Toast';

export function CopyButton({
  text,
  label = 'Copy',
  size = 'sm',
  iconOnly,
  variant = 'secondary',
}: {
  text: string | (() => string);
  label?: string;
  size?: ButtonSize;
  iconOnly?: boolean;
  variant?: 'secondary' | 'ghost';
}) {
  const [copied, setCopied] = useState(false);
  const toast = useToast();
  useEffect(() => {
    if (!copied) return;
    const t = window.setTimeout(() => setCopied(false), 1500);
    return () => window.clearTimeout(t);
  }, [copied]);
  return (
    <Button
      size={size}
      variant={variant}
      iconOnly={iconOnly}
      aria-label={iconOnly ? label : undefined}
      title={iconOnly ? label : undefined}
      icon={copied ? <Check size={14} className="text-success" /> : <Copy size={14} />}
      onClick={async () => {
        const ok = await copyText(typeof text === 'function' ? text() : text);
        if (ok) setCopied(true);
        else toast.error('Copy failed', 'Your browser blocked clipboard access. Select the text and copy it manually.');
      }}
    >
      {!iconOnly && (copied ? 'Copied' : label)}
    </Button>
  );
}
