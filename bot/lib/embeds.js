// Фабрика embed'ов: единые цвета и футер для всех постов бота.
const { EmbedBuilder } = require('discord.js');
const config = require('../config');

const COLORS = {
  green: 0x3fb55c, // позитив / sleeper / confirmed
  red: 0xc0413b, // trap / not confirmed
  blue: 0x2e86c1, // counter / инфо
  gold: 0xc8aa6e // дуэли / лидерборд / hall of fame
};

const FOOTER = `From the Counterplay database • ${config.siteUrl.replace(/^https?:\/\//, '')}`;

function embed(color) {
  return new EmbedBuilder().setColor(color).setFooter({ text: FOOTER });
}

module.exports = { COLORS, FOOTER, embed };
