#!/usr/bin/env node
// Shape checks that a lexicon linter cannot do.
// Usage: node tools/check.mjs <file-or-dir> [more...] [--json]
// Exit 1 when any error is found.

import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, extname, relative } from "node:path";

const EXT = new Set([".md", ".mdx", ".txt"]);

const LIMITS = {
  avgSentWarn: 18,
  avgSentError: 22,
  sentWarn: 30,
  sentError: 40,
  passiveWarn: 0.1,
  passiveError: 0.2,
  adverbWarnPer: 40,
  adverbErrorPer: 25,
  paraSentWarn: 4,
  paraSentError: 6,
  paraWordWarn: 80,
  paraWordError: 120,
  gradeWarn: 9,
  gradeError: 12,
  emDashWarnPer: 300,
  emDashErrorPer: 150,
};

const ADVERB_ALLOW = new Set([
  "only", "early", "family", "apply", "supply", "reply", "rely", "july",
  "assembly", "daily", "weekly", "monthly", "yearly", "hourly", "friendly",
  "likely", "ally", "belly", "jelly", "rally", "silly", "ugly", "holy",
]);

const SUPERLATIVES =
  /\b(world[- ]class|best[- ]in[- ]class|cutting[- ]edge|state[- ]of[- ]the[- ]art|next[- ]generation|game[- ]changing|revolutionary|industry[- ]leading|unmatched|unrivall?ed|the (?:fastest|smartest|most advanced|most powerful)|in the world)\b/i;

const CLAIM =
  /\b(measured|proven|proves|proved|benchmarks?|benchmarked|verified|we tested|i tested|demonstrated to|proven to)\b/i;

const PASSIVE =
  /\b(?:am|is|are|was|were|be|been|being)\s+(?:\w+ed|\w+en|done|made|given|taken|known|seen|shown|held|built|written|sold|sent|kept|left|found|put|set|run|used|called)\b/i;

const OPENERS =
  /^(in today's|in an era|in a world|as we all know|it'?s no secret|when it comes to|at the end of the day|let'?s |before we begin|imagine a world|ever wondered|first, let)/i;

const STRUCTURAL = /^\s*(#{1,6}\s|\||[-*+]\s|\d+[.)]\s|>|```|~~~|---+$|===+$)/;
const HEADING = /^(#{1,6})\s+(.*?)\s*#*\s*$/;

function collect(target, out = []) {
  const s = statSync(target);
  if (s.isDirectory()) {
    for (const name of readdirSync(target)) {
      if (name === "node_modules" || name === ".git") continue;
      collect(join(target, name), out);
    }
  } else if (EXT.has(extname(target).toLowerCase())) {
    out.push(target);
  }
  return out;
}

function stripCode(text) {
  return text
    .replace(/<!--\s*vale\s+off\s*-->[\s\S]*?<!--\s*vale\s+on\s*-->/g, (m) => m.replace(/[^\n]/g, " "))
    .replace(/<!--[\s\S]*?-->/g, (m) => m.replace(/[^\n]/g, " "))
    .replace(/```[\s\S]*?```/g, (m) => m.replace(/[^\n]/g, " "))
    .replace(/~~~[\s\S]*?~~~/g, (m) => m.replace(/[^\n]/g, " "))
    .replace(/`[^`\n]*`/g, (m) => " ".repeat(m.length));
}

function words(text) {
  return text.match(/[A-Za-z0-9][A-Za-z0-9'’\-]*/g) || [];
}

function sentences(text) {
  return text
    .split(/(?<=[.!?])["')\]]?\s+(?=[A-Z0-9"'(\[])/)
    .map((s) => s.trim())
    .filter((s) => s.length > 1);
}

function syllables(word) {
  let w = word.toLowerCase().replace(/[^a-z]/g, "");
  if (!w) return 0;
  if (w.length <= 3) return 1;
  w = w.replace(/(?:[^laeiouy]es|ed|[^laeiouy]e)$/, "");
  w = w.replace(/^y/, "");
  const groups = w.match(/[aeiouy]{1,2}/g);
  return groups ? groups.length : 1;
}

function grade(wordsCount, sentenceCount, syllableCount) {
  if (!wordsCount || !sentenceCount) return 0;
  return 0.39 * (wordsCount / sentenceCount) + 11.8 * (syllableCount / wordsCount) - 15.59;
}

function lineAt(text, index) {
  let line = 1;
  for (let i = 0; i < index && i < text.length; i++) if (text[i] === "\n") line++;
  return line;
}

function parse(file) {
  const raw = readFileSync(file, "utf8");
  const text = stripCode(raw);
  const findings = [];
  const add = (level, line, message, excerpt) => findings.push({ level, line, message, excerpt });

  if (!text.trim()) return { file, words: 0, sentences: 0, findings };

  // Headings
  let offset = 0;
  for (const lineText of text.split("\n")) {
    const h = HEADING.exec(lineText);
    if (h) {
      const heading = h[2].trim();
      const line = lineAt(text, offset);
      if (/^(introduction|overview|conclusion|summary|welcome|about this (page|guide|document)|getting started)$/i.test(heading)) {
        add("error", line, `empty heading "${heading}" - name what is under it`, heading);
      }
      if (/[.?!]$/.test(heading)) {
        add("warning", line, "heading ends with punctuation", heading);
      }
      const parts = heading.split(/\s+/).filter(Boolean);
      if (parts.length >= 3) {
        const capitalised = parts
          .slice(1)
          .filter((w) => /^[A-Z][a-z]/.test(w) && !/^[A-Z]{2,}$/.test(w));
        if (capitalised.length >= 2) {
          add("warning", line, 'heading is Title Case - use sentence case', heading);
        }
      }
    }
    offset += lineText.length + 1;
  }

  // Links
  for (const m of text.matchAll(/\[([^\]\n]{1,80})\]\(/g)) {
    if (/^(click here|here|read more|learn more|this link|this page|link)$/i.test(m[1].trim())) {
      add("error", lineAt(text, m.index), `ambiguous link text "${m[1].trim()}"`, m[0]);
    }
  }

  // Paragraphs
  const paragraphs = [];
  {
    const lines = text.split("\n");
    let buf = [];
    let start = 0;
    let index = 0;
    let inPara = false;
    for (const lineText of lines) {
      const blank = lineText.trim() === "";
      const structural = STRUCTURAL.test(lineText);
      if (blank || structural) {
        if (inPara && buf.length) {
          const body = buf.join(" ").replace(/\s+/g, " ").trim();
          if (body) paragraphs.push({ body, start, index });
        }
        buf = [];
        inPara = false;
      } else {
        if (!inPara) {
          inPara = true;
          start = lineAt(text, index);
          buf = [];
        }
        buf.push(lineText.trim());
      }
      index += lineText.length + 1;
    }
    if (inPara && buf.length) {
      paragraphs.push({ body: buf.join(" ").replace(/\s+/g, " ").trim(), start, index: text.length });
    }
  }

  let totalWords = 0;
  let totalSentences = 0;
  let totalSyllables = 0;
  let passiveCount = 0;
  let adverbCount = 0;
  let maxSent = 0;

  paragraphs.forEach(({ body, start }, pIndex) => {
    const ws = words(body);
    const ss = sentences(body);
    if (!ws.length) return;
    totalWords += ws.length;
    totalSentences += ss.length;
    for (const w of ws) totalSyllables += syllables(w);
    for (const w of ws) {
      if (/ly$/i.test(w) && !ADVERB_ALLOW.has(w.toLowerCase())) adverbCount++;
    }

    if (pIndex === 0) {
      const lead = ss[0] || "";
      if (OPENERS.test(lead)) {
        add("error", start, "opens with throat-clearing - start with the point", lead.slice(0, 80));
      }
      if (words(lead).length > 25) {
        add("warning", start, "opening sentence is long - state the point in one breath", lead.slice(0, 80));
      }
      if (/^(introduction|overview|in this article|welcome|we're excited|we are excited)/i.test(lead)) {
        add("error", start, "preamble - delete it", lead.slice(0, 80));
      }
    }

    if (ss.length > LIMITS.paraSentError) {
      add("error", start, `paragraph has ${ss.length} sentences (limit ${LIMITS.paraSentWarn})`, body.slice(0, 60));
    } else if (ss.length > LIMITS.paraSentWarn) {
      add("warning", start, `paragraph has ${ss.length} sentences (limit ${LIMITS.paraSentWarn})`, body.slice(0, 60));
    }
    if (ws.length > LIMITS.paraWordError) {
      add("error", start, `paragraph has ${ws.length} words (limit ${LIMITS.paraWordWarn})`, body.slice(0, 60));
    } else if (ws.length > LIMITS.paraWordWarn) {
      add("warning", start, `paragraph has ${ws.length} words (limit ${LIMITS.paraWordWarn})`, body.slice(0, 60));
    }

    for (const s of ss) {
      const sw = words(s).length;
      maxSent = Math.max(maxSent, sw);
      if (sw > LIMITS.sentError) {
        add("error", start, `sentence is ${sw} words (limit ${LIMITS.sentError})`, s.slice(0, 70));
      } else if (sw > LIMITS.sentWarn) {
        add("warning", start, `sentence is ${sw} words (limit ${LIMITS.sentWarn})`, s.slice(0, 70));
      }
      if (PASSIVE.test(s)) passiveCount++;
      if (SUPERLATIVES.test(s) && !/[0-9]/.test(s) && !/\]\(|https?:\/\//.test(s)) {
        add("error", start, "superlative without a number or link in the sentence", s.slice(0, 70));
      }
      if (CLAIM.test(s) && !/[0-9]/.test(s) && !/\]\(|https?:\/\//.test(s)) {
        add("error", start, "unverifiable claim: no number or public link in the sentence", s.slice(0, 70));
      }
    }
  });

  const avg = totalSentences ? totalWords / totalSentences : 0;
  const passiveRatio = totalSentences ? passiveCount / totalSentences : 0;
  const g = grade(totalWords, totalSentences, totalSyllables);
  const emDashes = (text.match(/—/g) || []).length;

  const docLine = 1;
  if (avg > LIMITS.avgSentError) add("error", docLine, `average sentence length ${avg.toFixed(1)} (limit ${LIMITS.avgSentWarn})`);
  else if (avg > LIMITS.avgSentWarn) add("warning", docLine, `average sentence length ${avg.toFixed(1)} (limit ${LIMITS.avgSentWarn})`);

  if (passiveRatio > LIMITS.passiveError) add("error", docLine, `passive voice in ${(passiveRatio * 100).toFixed(0)}% of sentences (limit 10%)`);
  else if (passiveRatio > LIMITS.passiveWarn) add("warning", docLine, `passive voice in ${(passiveRatio * 100).toFixed(0)}% of sentences (limit 10%)`);

  if (adverbCount && totalWords / adverbCount < LIMITS.adverbErrorPer) {
    add("error", docLine, `-ly adverbs every ${(totalWords / adverbCount).toFixed(0)} words (limit every ${LIMITS.adverbWarnPer})`);
  } else if (adverbCount && totalWords / adverbCount < LIMITS.adverbWarnPer) {
    add("warning", docLine, `-ly adverbs every ${(totalWords / adverbCount).toFixed(0)} words (limit every ${LIMITS.adverbWarnPer})`);
  }

  if (g > LIMITS.gradeError) add("error", docLine, `reading grade ${g.toFixed(1)} (limit ${LIMITS.gradeWarn})`);
  else if (g > LIMITS.gradeWarn) add("warning", docLine, `reading grade ${g.toFixed(1)} (limit ${LIMITS.gradeWarn})`);

  if (emDashes > totalWords / LIMITS.emDashErrorPer) add("error", docLine, `${emDashes} em dashes in ${totalWords} words`);
  else if (emDashes > totalWords / LIMITS.emDashWarnPer) add("warning", docLine, `${emDashes} em dashes in ${totalWords} words`);

  return {
    file,
    words: totalWords,
    sentences: totalSentences,
    avg,
    maxSent,
    passive: passiveRatio,
    grade: g,
    emDashes,
    findings,
  };
}

function main() {
  const args = process.argv.slice(2);
  const json = args.includes("--json");
  const targets = args.filter((a) => !a.startsWith("--"));
  if (!targets.length) {
    console.error("usage: node tools/check.mjs <file-or-dir> [more...] [--json]");
    process.exit(2);
  }

  const files = targets.flatMap((t) => collect(t));
  const results = files.map(parse);
  const errors = results.flatMap((r) => r.findings.filter((f) => f.level === "error"));
  const warnings = results.flatMap((r) => r.findings.filter((f) => f.level === "warning"));

  if (json) {
    console.log(JSON.stringify({ files: results, errors: errors.length, warnings: warnings.length }, null, 2));
    process.exit(errors.length ? 1 : 0);
  }

  console.log("");
  console.log("file".padEnd(34) + "words".padStart(7) + "sent".padStart(6) + "avg".padStart(6) + "max".padStart(5) + "pass".padStart(6) + "grade".padStart(7) + "em".padStart(4));
  console.log("-".repeat(75));
  for (const r of results) {
    const name = relative(process.cwd(), r.file);
    console.log(
      name.padEnd(34) +
        String(r.words).padStart(7) +
        String(r.sentences).padStart(6) +
        (r.avg || 0).toFixed(1).padStart(6) +
        String(r.maxSent || 0).padStart(5) +
        `${Math.round((r.passive || 0) * 100)}%`.padStart(6) +
        (r.grade || 0).toFixed(1).padStart(7) +
        String(r.emDashes || 0).padStart(4)
    );
  }
  console.log("");

  for (const r of results) {
    for (const f of r.findings) {
      const where = `${relative(process.cwd(), r.file)}:${f.line}`;
      const label = f.level === "error" ? "ERROR" : "WARN ";
      console.log(`${label} ${where}  ${f.message}${f.excerpt ? `\n          > ${f.excerpt}` : ""}`);
    }
  }

  console.log("");
  console.log(`${errors.length} error(s), ${warnings.length} warning(s)`);
  process.exit(errors.length ? 1 : 0);
}

main();
