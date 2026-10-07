import { useId } from 'react'
import { checkName, checkText } from '../lib/names'

type Props = {
  label: string
  value: string
  onChange: (value: string) => void
  maxLength: number
  serverError: string | null
  autoFocus?: boolean
  freeText?: boolean
}

export function NameField({ label, value, onChange, maxLength, serverError, autoFocus, freeText }: Props) {
  const localError = value.length > 0 ? (freeText ? checkText : checkName)(value, maxLength) : null
  const hint = freeText ? `At most ${maxLength} characters.` : `Letters, digits and underscores, at most ${maxLength} characters.`
  const error = localError ?? serverError
  const id = useId()
  return (
    <div className="field">
      <label className="field-label" htmlFor={id}>{label}</label>
      <input
        id={id}
        className={error ? 'input input-error' : 'input'}
        value={value}
        autoFocus={autoFocus}
        spellCheck={false}
        aria-invalid={error ? true : undefined}
        aria-describedby={`${id}-hint`}
        onChange={(event) => onChange(event.target.value)}
      />
      <span id={`${id}-hint`} className="field-hint">{error ?? hint}</span>
    </div>
  )
}
