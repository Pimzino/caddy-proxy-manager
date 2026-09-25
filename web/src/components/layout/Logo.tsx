export function Logo({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 32 32" className={className} aria-hidden>
      <rect width="32" height="32" rx="6" className="fill-accent" />
      <path
        d="M9 11.5 16 7.5l7 4v9l-7 4-7-4zM16 15.5v9M9 11.5l7 4 7-4"
        fill="none"
        stroke="currentColor"
        className="text-accent-fg"
        strokeWidth="2.2"
        strokeLinejoin="round"
      />
    </svg>
  );
}
