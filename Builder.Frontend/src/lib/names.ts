const allowed = /^[A-Za-z0-9_]+$/

export function checkName(name: string, maxLength: number): string | null {
  if (name.length === 0) return 'A name is required.'
  if (name.length > maxLength) return `A name can have at most ${maxLength} characters.`
  if (!allowed.test(name)) return 'A name can only contain letters, digits and underscores.'
  return null
}

export function checkText(name: string, maxLength: number): string | null {
  if (name.trim().length === 0) return 'A name is required.'
  if (name.length > maxLength) return `A name can have at most ${maxLength} characters.`
  return null
}
