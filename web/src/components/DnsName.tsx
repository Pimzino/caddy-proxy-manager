import { Fragment } from 'react';

/** A DNS name that wraps only after its dots. */
export function DnsName({ name }: { name: string }) {
  const labels = name.split('.');
  return (
    <>
      {labels.map((l, i) => (
        <Fragment key={i}>
          {/* No break inside a label, not even after the hyphen of "_acme-challenge". */}
          <span className="whitespace-nowrap">{l}</span>
          {i < labels.length - 1 && (
            <>
              .<wbr />
            </>
          )}
        </Fragment>
      ))}
    </>
  );
}
